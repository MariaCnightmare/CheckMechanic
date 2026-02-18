using System.Net;
using System.Management;
using System.Text;
using System.Text.Json;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using CheckMechanic.Shared;
using LibreHardwareMonitor.Hardware;

const string version = "1.0";
const int port = 17805;
var diagLogPath = Path.Combine(Path.GetTempPath(), "checkmechanic_sensorhelper.log");

void Log(string message)
{
    var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}";
    Console.WriteLine(line);
    try
    {
        File.AppendAllText(diagLogPath, line + Environment.NewLine);
    }
    catch
    {
    }
}

Log("sensor helper starting");

Computer? monitor = null;
string? sensorInitError = "sensor backend initializing";
var monitorSync = new object();

_ = Task.Run(() =>
{
    try
    {
        var m = new Computer
        {
            IsCpuEnabled = true,
            IsMotherboardEnabled = true,
            IsGpuEnabled = false,
            IsMemoryEnabled = false,
            IsStorageEnabled = false,
            IsNetworkEnabled = false,
        };
        m.Open();
        for (var i = 0; i < 3; i++)
        {
            foreach (var hw in m.Hardware)
            {
                hw.Update();
            }
            Thread.Sleep(200);
        }
        lock (monitorSync)
        {
            monitor = m;
            sensorInitError = null;
        }
        Log("sensor backend initialized");
    }
    catch (Exception ex)
    {
        lock (monitorSync)
        {
            monitor = null;
            sensorInitError = "sensor backend init failed";
        }
        Log($"sensor backend init failed: {ex.GetType().Name}: {ex.Message}");
    }
});

var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{port}/");

try
{
    listener.Start();
    Log($"listening on http://127.0.0.1:{port}");
}
catch (Exception ex)
{
    Log($"listener start failed: {ex.GetType().Name}: {ex.Message}");
    return;
}

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    try
    {
        listener.Stop();
        listener.Close();
    }
    catch
    {
    }

    lock (monitorSync)
    {
        monitor?.Close();
    }
    Environment.Exit(0);
};

while (listener.IsListening)
{
    HttpListenerContext ctx;
    try
    {
        ctx = listener.GetContext();
    }
    catch (Exception)
    {
        break;
    }

    _ = Task.Run(async () =>
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (path.Equals("/health", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(ctx.Response, 200, new HealthResponse { Ok = true, Version = version });
                return;
            }

            if (path.Equals("/v1/telemetry", StringComparison.OrdinalIgnoreCase))
            {
                Computer? currentMonitor;
                string? currentInitError;
                lock (monitorSync)
                {
                    currentMonitor = monitor;
                    currentInitError = sensorInitError;
                }

                var body = new TelemetryResponse
                {
                    Ts = DateTimeOffset.Now,
                    Cpu = ReadCpuTelemetry(currentMonitor, currentInitError),
                };
                await WriteJsonAsync(ctx.Response, 200, body);
                return;
            }

            if (path.Equals("/v1/sensors", StringComparison.OrdinalIgnoreCase))
            {
                Computer? currentMonitor;
                lock (monitorSync)
                {
                    currentMonitor = monitor;
                }

                if (currentMonitor is null)
                {
                    await WriteJsonAsync(ctx.Response, 200, new { cpu_temperature_candidates = Array.Empty<object>() });
                    return;
                }

                var sensors = CollectTemperatureSensors(currentMonitor)
                    .Where(s => IsCpuLike(s.HardwarePath, s.SensorName))
                    .Select(s => new
                    {
                        name = s.SensorName,
                        type = ClassifyCpuCandidateType(s.SensorName),
                        value = s.Value,
                        hardware_path = s.HardwarePath,
                    })
                    .ToList();
                await WriteJsonAsync(ctx.Response, 200, new { cpu_temperature_candidates = sensors });
                return;
            }

            await WriteJsonAsync(ctx.Response, 404, new { error = "not found" });
        }
        catch (Exception ex)
        {
            Log($"request handler failed: {ex.GetType().Name}: {ex.Message}");
            try
            {
                await WriteJsonAsync(ctx.Response, 500, new { error = "internal error" });
            }
            catch
            {
            }
        }
    });
}

static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, object payload)
{
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    var bytes = Encoding.UTF8.GetBytes(json);
    response.StatusCode = statusCode;
    response.ContentType = "application/json; charset=utf-8";
    response.ContentLength64 = bytes.Length;
    await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
    response.OutputStream.Close();
}

static CpuTelemetry ReadCpuTelemetry(Computer? monitor, string? sensorInitError)
{
    var util = ReadCpuUtilization(monitor);
    var providerErrors = new List<string>();

    // Temperature-required mode: Core Temp shared memory is authoritative.
    if (TryReadCoreTempTemperature(out var coreTempValue, out var coreTempLabel, out var coreTempError))
    {
        return new CpuTelemetry
        {
            TempC = coreTempValue,
            UtilPercent = util,
            Label = coreTempLabel,
            Source = "CoreTempSharedMemory",
            ProviderUsed = "coretemp",
            Error = null,
            ErrorCode = null,
            ProviderErrors = providerErrors,
        };
    }
    if (!string.IsNullOrWhiteSpace(coreTempError))
    {
        providerErrors.Add($"coretemp:{coreTempError}");
    }

    // LHM/WMI are diagnostics-only in temperature-required mode.
    CollectLhmDiagnosticErrors(monitor, sensorInitError, providerErrors);
    if (!TryReadWmiTemperature(out _, out _, out var wmiErrorCode) && !string.IsNullOrWhiteSpace(wmiErrorCode))
    {
        providerErrors.Add($"wmi:{wmiErrorCode}");
    }

    return new CpuTelemetry
    {
        TempC = null,
        UtilPercent = util,
        Label = null,
        Source = "unavailable",
        ProviderUsed = null,
        Error = "temperature unavailable",
        ErrorCode = "TEMP_SENSOR_VALUES_UNAVAILABLE",
        ProviderErrors = providerErrors,
    };
}

static bool IsCpuLike(string hardwarePath, string sensorName)
{
    var h = hardwarePath.ToLowerInvariant();
    var s = sensorName.ToLowerInvariant();
    return h.Contains("cpu") || s.Contains("cpu") || s.Contains("package") || s.Contains("tctl") || s.Contains("tdie");
}

static string ClassifyCpuCandidateType(string sensorName)
{
    var name = sensorName.ToLowerInvariant();
    if (name.Contains("package"))
    {
        return "package";
    }
    if (name.Contains("core max") || name.Contains("coremax"))
    {
        return "core_max";
    }
    if (name.Contains("core average") || name.Contains("core avg") || name.Contains("average"))
    {
        return "core_average";
    }
    return "other";
}

static List<TempSensorSnapshot> CollectTemperatureSensors(Computer monitor)
{
    var sensors = new List<TempSensorSnapshot>();
    foreach (var hw in monitor.Hardware)
    {
        WalkHardware(hw, hw.Name, sensors);
    }
    return sensors;
}

static void WalkHardware(IHardware hw, string path, List<TempSensorSnapshot> sensors)
{
    hw.Update();
    foreach (var s in hw.Sensors.Where(x => x.SensorType == SensorType.Temperature))
    {
        sensors.Add(new TempSensorSnapshot(path, s.Name, s.Value, s.Min, s.Max));
    }
    foreach (var sub in hw.SubHardware)
    {
        WalkHardware(sub, $"{path} > {sub.Name}", sensors);
    }
}

static double? ReadCpuUtilization(Computer? monitor)
{
    if (monitor is null)
    {
        return null;
    }

    try
    {
        var loadSensors = new List<ISensor>();
        foreach (var hw in monitor.Hardware.Where(h => h.HardwareType == HardwareType.Cpu))
        {
            hw.Update();
            loadSensors.AddRange(hw.Sensors.Where(s => s.SensorType == SensorType.Load));
            foreach (var sub in hw.SubHardware)
            {
                sub.Update();
                loadSensors.AddRange(sub.Sensors.Where(s => s.SensorType == SensorType.Load));
            }
        }

        var total = loadSensors.FirstOrDefault(s =>
            s.Name.Contains("total", StringComparison.OrdinalIgnoreCase) && s.Value is not null);
        if (total?.Value is float totalValue)
        {
            return totalValue;
        }

        var first = loadSensors.FirstOrDefault(s => s.Value is not null);
        if (first?.Value is float firstValue)
        {
            return firstValue;
        }

        return null;
    }
    catch
    {
        return null;
    }
}

static bool TryReadWmiTemperature(out double? tempC, out string? label, out string? errorCode)
{
    try
    {
        using var searcher = new ManagementObjectSearcher(
            @"root\WMI",
            "SELECT CurrentTemperature, InstanceName FROM MSAcpi_ThermalZoneTemperature");
        var results = searcher.Get();
        foreach (var obj in results.Cast<ManagementObject>())
        {
            var raw = obj["CurrentTemperature"];
            if (raw is null)
            {
                continue;
            }

            var kelvinX10 = Convert.ToDouble(raw);
            if (kelvinX10 <= 0)
            {
                continue;
            }

            var celsius = (kelvinX10 / 10.0) - 273.15;
            if (celsius is > -40 and < 150)
            {
                tempC = celsius;
                label = obj["InstanceName"]?.ToString() ?? "WMI Thermal Zone";
                errorCode = null;
                return true;
            }
        }

        tempC = null;
        label = null;
        errorCode = "TEMP_WMI_THERMAL_ZONE_NOT_FOUND";
        return false;
    }
    catch
    {
        tempC = null;
        label = null;
        errorCode = "TEMP_WMI_QUERY_FAILED";
        return false;
    }
}

static bool TryReadCoreTempTemperature(out double? tempC, out string? label, out string? errorCode)
{
    static bool TryFromMapping(string mappingName, out double? mappingTempC, out string? mappingLabel, out string? mappingErrorCode)
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(mappingName, MemoryMappedFileRights.Read);
            var size = Marshal.SizeOf<CoreTempSharedData>();
            using var accessor = mmf.CreateViewAccessor(0, size, MemoryMappedFileAccess.Read);
            var raw = new byte[size];
            accessor.ReadArray(0, raw, 0, raw.Length);
            var handle = GCHandle.Alloc(raw, GCHandleType.Pinned);
            try
            {
                var data = Marshal.PtrToStructure<CoreTempSharedData>(handle.AddrOfPinnedObject());
                var coreCount = (int)Math.Clamp(data.CoreCount, 0u, 256u);
                if (coreCount <= 0)
                {
                    mappingTempC = null;
                    mappingLabel = null;
                    mappingErrorCode = "TEMP_CORETEMP_NO_CORES";
                    return false;
                }

                var temps = data.Temps.Take(coreCount).Where(t => t is > -40 and < 150).ToList();
                if (temps.Count == 0)
                {
                    mappingTempC = null;
                    mappingLabel = null;
                    mappingErrorCode = "TEMP_CORETEMP_VALUES_UNAVAILABLE";
                    return false;
                }

                mappingTempC = temps.Max();
                mappingLabel = "Core Temp Shared Memory / Core Max";
                mappingErrorCode = null;
                return true;
            }
            finally
            {
                handle.Free();
            }
        }
        catch (FileNotFoundException)
        {
            mappingTempC = null;
            mappingLabel = null;
            mappingErrorCode = "TEMP_CORETEMP_SHM_NOT_FOUND";
            return false;
        }
        catch
        {
            mappingTempC = null;
            mappingLabel = null;
            mappingErrorCode = "TEMP_CORETEMP_READ_FAILED";
            return false;
        }
    }

    if (TryFromMapping("CoreTempMappingObject", out tempC, out label, out var baseError))
    {
        errorCode = baseError;
        return true;
    }
    if (TryFromMapping("CoreTempMappingObjectEx", out tempC, out label, out var exError))
    {
        errorCode = exError;
        return true;
    }

    tempC = null;
    label = null;
    errorCode = exError ?? baseError ?? "TEMP_CORETEMP_UNAVAILABLE";
    return false;
}

static void CollectLhmDiagnosticErrors(Computer? monitor, string? sensorInitError, List<string> providerErrors)
{
    if (monitor is null)
    {
        var initCode = sensorInitError == "sensor backend initializing"
            ? "TEMP_BACKEND_INITIALIZING"
            : "TEMP_BACKEND_INIT_FAILED";
        providerErrors.Add($"lhm:{initCode}");
        return;
    }

    try
    {
        var sensors = CollectTemperatureSensors(monitor).Where(s => IsCpuLike(s.HardwarePath, s.SensorName)).ToList();
        if (sensors.Count == 0)
        {
            providerErrors.Add("lhm:TEMP_SENSOR_NOT_FOUND");
            return;
        }

        var anyValue = sensors.Any(s =>
            (s.Value is float v && v is > -40 and < 150) ||
            (s.Max is float max && max is > -40 and < 150) ||
            (s.Min is float min && min is > -40 and < 150));
        if (anyValue)
        {
            providerErrors.Add("lhm:TEMP_DIAGNOSTIC_VALUE_AVAILABLE");
        }
        else
        {
            providerErrors.Add("lhm:TEMP_SENSOR_VALUES_UNAVAILABLE");
        }
    }
    catch
    {
        providerErrors.Add("lhm:TEMP_SENSOR_READ_FAILED");
    }
}

record TempSensorSnapshot(string HardwarePath, string SensorName, float? Value, float? Min, float? Max);

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
struct CoreTempSharedData
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
    public uint[] Loads;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
    public uint[] TjMax;
    public uint CoreCount;
    public uint CpuCount;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
    public float[] Temps;
    public float Vid;
    public float CpuSpeed;
    public float FsbSpeed;
    public float Multiplier;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 100)]
    public string CpuName;
    public byte Fahrenheit;
    public byte DeltaToTjMax;
}
