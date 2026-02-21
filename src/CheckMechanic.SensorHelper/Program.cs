using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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

var telemetrySampler = new TelemetrySampler();

_ = Task.Run(() =>
{
    try
    {
        var m = new Computer
        {
            IsCpuEnabled = true,
            IsMotherboardEnabled = true,
            IsGpuEnabled = true,
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

    telemetrySampler.Dispose();
    Environment.Exit(0);
};

while (listener.IsListening)
{
    HttpListenerContext ctx;
    try
    {
        ctx = listener.GetContext();
    }
    catch
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

                var cpu = ReadCpuTelemetry(currentMonitor, currentInitError);
                var body = new TelemetryResponse
                {
                    Ts = DateTimeOffset.Now,
                    Cpu = cpu,
                    Memory = telemetrySampler.ReadMemoryTelemetry(),
                    Disk = telemetrySampler.ReadDiskTelemetry(),
                    Net = telemetrySampler.ReadNetworkTelemetry(),
                    Gpu = ReadGpuTelemetry(currentMonitor),
                    Battery = telemetrySampler.ReadBatteryTelemetry(),
                };
                await WriteJsonAsync(ctx.Response, 200, body);
                return;
            }

            if (path.Equals("/v1/profile", StringComparison.OrdinalIgnoreCase))
            {
                Computer? currentMonitor;
                string? currentInitError;
                lock (monitorSync)
                {
                    currentMonitor = monitor;
                    currentInitError = sensorInitError;
                }

                var cpu = ReadCpuTelemetry(currentMonitor, currentInitError);
                var profile = BuildSystemProfile(cpu, telemetrySampler);
                await WriteJsonAsync(ctx.Response, 200, profile);
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
    var coreInfo = ReadCpuCoreInfo();
    var providerErrors = new List<string>();

    var lhmTemps = ReadLhmCpuTempHints(monitor);
    var clockMhz = ReadCpuClockMhz(monitor, out var clockProviderError);
    var powerW = ReadCpuPowerW(monitor);
    if (!string.IsNullOrWhiteSpace(clockProviderError))
    {
        providerErrors.Add($"clock:{clockProviderError}");
    }

    if (TryReadCoreTempTemperature(out var coreTempValue, out var coreTempLabel, out var coreTempError))
    {
        return new CpuTelemetry
        {
            TempC = coreTempValue,
            UtilPercent = util,
            LogicalCores = coreInfo.LogicalCores,
            PhysicalCores = coreInfo.PhysicalCores,
            Brand = coreInfo.Brand,
            ClockMhz = clockMhz,
            PowerW = powerW,
            TempPackageC = coreTempValue,
            TempCoreMaxC = lhmTemps.CoreMaxC ?? coreTempValue,
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

    CollectLhmDiagnosticErrors(monitor, sensorInitError, providerErrors);
    if (!TryReadWmiTemperature(out _, out _, out var wmiErrorCode) && !string.IsNullOrWhiteSpace(wmiErrorCode))
    {
        providerErrors.Add($"wmi:{wmiErrorCode}");
    }

    return new CpuTelemetry
    {
        TempC = null,
        UtilPercent = util,
        LogicalCores = coreInfo.LogicalCores,
        PhysicalCores = coreInfo.PhysicalCores,
        Brand = coreInfo.Brand,
        ClockMhz = clockMhz,
        PowerW = powerW,
        TempPackageC = lhmTemps.PackageC,
        TempCoreMaxC = lhmTemps.CoreMaxC,
        Label = lhmTemps.Label,
        Source = "unavailable",
        ProviderUsed = null,
        Error = "temperature unavailable",
        ErrorCode = "TEMP_SENSOR_VALUES_UNAVAILABLE",
        ProviderErrors = providerErrors,
    };
}

static GpuTelemetry ReadGpuTelemetry(Computer? monitor)
{
    var name = ReadGpuName();
    var util = ReadGpuUtilPercentFromLhm(monitor) ?? ReadGpuUtilPercentFromPerformanceCounter();
    var (coreClock, memClock) = ReadGpuClocksFromLhm(monitor);
    var (vramUsedMb, vramTotalMb) = ReadGpuVramMetricsMb();

    return new GpuTelemetry
    {
        Name = name,
        Vendor = InferGpuVendor(name),
        DriverVersion = ReadGpuDriverVersion(),
        UtilPercent = util,
        VramUsedMb = vramUsedMb,
        VramTotalMb = vramTotalMb ?? ReadGpuVramTotalMb(),
        TemperatureC = ReadGpuTemperatureFromLhm(monitor),
        CoreClockMhz = coreClock,
        MemoryClockMhz = memClock,
    };
}

static SystemProfileDto BuildSystemProfile(CpuTelemetry cpu, TelemetrySampler sampler)
{
    var osVersion = Environment.OSVersion.Version;
    var major = osVersion.Major >= 10
        ? (osVersion.Build >= 22000 ? "11" : "10")
        : osVersion.Major.ToString();
    var buildBucket = osVersion.Build > 0
        ? $"{osVersion.Build / 100}xx"
        : null;

    var memory = sampler.ReadMemoryTelemetry();
    var storage = ReadPrimaryStorageInfo();

    return new SystemProfileDto
    {
        OsMajor = major,
        OsBuildBucket = buildBucket,
        CpuBrand = cpu.Brand,
        PhysicalCores = cpu.PhysicalCores,
        LogicalCores = cpu.LogicalCores,
        MemoryTotalGbBucket = BucketByGb(memory.TotalGb),
        GpuName = ReadGpuName(),
        GpuDriverVersion = ReadGpuDriverVersion(),
        StoragePrimaryType = storage.Type,
        StorageModel = storage.Model,
        StorageBusType = storage.BusType,
        StorageTotalGbBucket = BucketByGb(ReadSystemDriveTotalGb()),
        DeviceClass = DetectDeviceClass(),
        TempProvider = string.IsNullOrWhiteSpace(cpu.ProviderUsed) ? "unavailable" : cpu.ProviderUsed,
        MachineVendor = ReadMachineVendor(),
        MachineModel = ReadMachineModel(),
        UptimeHours = ReadUptimeHours(),
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
    if (name.Contains("package")) return "package";
    if (name.Contains("core max") || name.Contains("coremax")) return "core_max";
    if (name.Contains("core average") || name.Contains("core avg") || name.Contains("average")) return "core_average";
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

        var total = loadSensors.FirstOrDefault(s => s.Name.Contains("total", StringComparison.OrdinalIgnoreCase) && s.Value is not null);
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

static CpuCoreInfo ReadCpuCoreInfo()
{
    try
    {
        using var searcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
        var items = searcher.Get().Cast<ManagementObject>().ToList();
        if (items.Count == 0)
        {
            return new CpuCoreInfo(null, Environment.ProcessorCount, null);
        }

        var first = items[0];
        var brand = first["Name"]?.ToString()?.Trim();
        var physical = items.Sum(x => Convert.ToInt32(x["NumberOfCores"] ?? 0));
        var logical = items.Sum(x => Convert.ToInt32(x["NumberOfLogicalProcessors"] ?? 0));

        return new CpuCoreInfo(
            logical > 0 ? logical : Environment.ProcessorCount,
            physical > 0 ? physical : null,
            string.IsNullOrWhiteSpace(brand) ? null : brand);
    }
    catch
    {
        return new CpuCoreInfo(Environment.ProcessorCount, null, null);
    }
}

static (double? PackageC, double? CoreMaxC, string? Label) ReadLhmCpuTempHints(Computer? monitor)
{
    if (monitor is null)
    {
        return (null, null, null);
    }

    try
    {
        var candidates = CollectTemperatureSensors(monitor).Where(s => IsCpuLike(s.HardwarePath, s.SensorName)).ToList();
        if (candidates.Count == 0)
        {
            return (null, null, null);
        }

        static double? FromSensorValue(TempSensorSnapshot s)
        {
            if (s.Value is float sv && sv is > -40 and < 150) return sv;
            if (s.Max is float smx && smx is > -40 and < 150) return smx;
            if (s.Min is float smn && smn is > -40 and < 150) return smn;
            return null;
        }

        var package = candidates.FirstOrDefault(s => s.SensorName.Contains("package", StringComparison.OrdinalIgnoreCase));
        var packageValue = package is null ? null : FromSensorValue(package);

        var coreMax = candidates
            .Where(s => s.SensorName.Contains("core max", StringComparison.OrdinalIgnoreCase) || s.SensorName.Contains("core", StringComparison.OrdinalIgnoreCase))
            .Select(FromSensorValue)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .DefaultIfEmpty(double.NaN)
            .Max();

        var coreMaxValue = double.IsNaN(coreMax) ? (double?)null : coreMax;
        var label = package?.SensorName ?? candidates[0].SensorName;
        return (packageValue, coreMaxValue, label);
    }
    catch
    {
        return (null, null, null);
    }
}

static double? ReadCpuClockMhz(Computer? monitor, out string? providerError)
{
    providerError = null;

    if (monitor is not null)
    {
        try
        {
            var clocks = new List<float>();
            foreach (var hw in monitor.Hardware.Where(h => h.HardwareType == HardwareType.Cpu))
            {
                hw.Update();
                clocks.AddRange(hw.Sensors.Where(s => s.SensorType == SensorType.Clock && s.Value is not null).Select(s => s.Value!.Value));
            }

            if (clocks.Count > 0)
            {
                return clocks.Average();
            }
        }
        catch
        {
        }
    }

    try
    {
        using var searcher = new ManagementObjectSearcher("SELECT CurrentClockSpeed FROM Win32_Processor");
        var values = searcher.Get().Cast<ManagementObject>()
            .Select(x => x["CurrentClockSpeed"])
            .Where(x => x is not null)
            .Select(Convert.ToDouble)
            .ToList();
        if (values.Count > 0)
        {
            return values.Average();
        }
    }
    catch
    {
    }

    try
    {
        using var searcher = new ManagementObjectSearcher("SELECT ProcessorFrequency FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name='_Total'");
        var values = searcher.Get().Cast<ManagementObject>()
            .Select(x => x["ProcessorFrequency"])
            .Where(x => x is not null)
            .Select(Convert.ToDouble)
            .Where(x => x > 0)
            .ToList();
        if (values.Count > 0)
        {
            return values.Average();
        }
    }
    catch
    {
    }

    try
    {
        using var perf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true);
        perf.NextValue();
        Thread.Sleep(200);
        var percentPerf = perf.NextValue();
        if (percentPerf > 0)
        {
            using var baseFreqSearcher = new ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor");
            var baseFreq = baseFreqSearcher.Get().Cast<ManagementObject>()
                .Select(x => x["MaxClockSpeed"])
                .Where(x => x is not null)
                .Select(Convert.ToDouble)
                .Where(x => x > 0)
                .DefaultIfEmpty(0)
                .Average();
            if (baseFreq > 0)
            {
                return baseFreq * (percentPerf / 100d);
            }
        }
    }
    catch
    {
    }

    providerError = "CPU_CLOCK_UNAVAILABLE";
    return null;
}

static double? ReadCpuPowerW(Computer? monitor)
{
    if (monitor is null)
    {
        return null;
    }

    try
    {
        var powerSensors = new List<float>();
        foreach (var hw in monitor.Hardware.Where(h => h.HardwareType == HardwareType.Cpu))
        {
            hw.Update();
            powerSensors.AddRange(hw.Sensors.Where(s => s.SensorType == SensorType.Power && s.Value is not null).Select(s => s.Value!.Value));
            foreach (var sub in hw.SubHardware)
            {
                sub.Update();
                powerSensors.AddRange(sub.Sensors.Where(s => s.SensorType == SensorType.Power && s.Value is not null).Select(s => s.Value!.Value));
            }
        }

        if (powerSensors.Count == 0)
        {
            return null;
        }

        return powerSensors.Max();
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
        using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature, InstanceName FROM MSAcpi_ThermalZoneTemperature");
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

    string? baseError;
    if (TryFromMapping("CoreTempMappingObject", out tempC, out label, out baseError))
    {
        errorCode = null;
        return true;
    }

    string? exError;
    if (TryFromMapping("CoreTempMappingObjectEx", out tempC, out label, out exError))
    {
        errorCode = null;
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
        providerErrors.Add(anyValue ? "lhm:TEMP_DIAGNOSTIC_VALUE_AVAILABLE" : "lhm:TEMP_SENSOR_VALUES_UNAVAILABLE");
    }
    catch
    {
        providerErrors.Add("lhm:TEMP_SENSOR_READ_FAILED");
    }
}

static double? ReadGpuUtilPercentFromLhm(Computer? monitor)
{
    if (monitor is null)
    {
        return null;
    }

    try
    {
        var values = new List<float>();
        foreach (var hw in monitor.Hardware.Where(h => h.HardwareType == HardwareType.GpuAmd || h.HardwareType == HardwareType.GpuNvidia || h.HardwareType == HardwareType.GpuIntel))
        {
            hw.Update();
            values.AddRange(hw.Sensors.Where(s => s.SensorType == SensorType.Load && s.Value is not null).Select(s => s.Value!.Value));
            foreach (var sub in hw.SubHardware)
            {
                sub.Update();
                values.AddRange(sub.Sensors.Where(s => s.SensorType == SensorType.Load && s.Value is not null).Select(s => s.Value!.Value));
            }
        }

        if (values.Count == 0)
        {
            return null;
        }

        return values.Max();
    }
    catch
    {
        return null;
    }
}

static double? ReadGpuTemperatureFromLhm(Computer? monitor)
{
    if (monitor is null)
    {
        return null;
    }

    try
    {
        var values = new List<float>();
        foreach (var hw in monitor.Hardware.Where(h => h.HardwareType == HardwareType.GpuAmd || h.HardwareType == HardwareType.GpuNvidia || h.HardwareType == HardwareType.GpuIntel))
        {
            hw.Update();
            values.AddRange(hw.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is not null).Select(s => s.Value!.Value));
            foreach (var sub in hw.SubHardware)
            {
                sub.Update();
                values.AddRange(sub.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is not null).Select(s => s.Value!.Value));
            }
        }

        return values.Count > 0 ? values.Max() : null;
    }
    catch
    {
        return null;
    }
}

static double? ReadGpuUtilPercentFromPerformanceCounter()
{
    try
    {
        var category = new PerformanceCounterCategory("GPU Engine");
        var instances = category.GetInstanceNames()
            .Where(n => n.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (instances.Count == 0)
        {
            return null;
        }

        double sum = 0;
        foreach (var name in instances)
        {
            using var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true);
            sum += counter.NextValue();
        }

        return Math.Clamp(sum, 0, 100);
    }
    catch
    {
        return null;
    }
}

static (double? UsedMb, double? TotalMb) ReadGpuVramMetricsMb()
{
    try
    {
        var category = new PerformanceCounterCategory("GPU Adapter Memory");
        var instances = category.GetInstanceNames();
        double dedicatedUsedBytes = 0;
        double sharedUsedBytes = 0;
        double dedicatedLimitBytes = 0;
        double sharedLimitBytes = 0;
        foreach (var name in instances)
        {
            using var dedicatedCounter = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", name, true);
            using var sharedCounter = new PerformanceCounter("GPU Adapter Memory", "Shared Usage", name, true);
            using var dedicatedLimitCounter = new PerformanceCounter("GPU Adapter Memory", "Dedicated Limit", name, true);
            using var sharedLimitCounter = new PerformanceCounter("GPU Adapter Memory", "Shared Limit", name, true);
            dedicatedUsedBytes += dedicatedCounter.NextValue();
            sharedUsedBytes += sharedCounter.NextValue();
            dedicatedLimitBytes += dedicatedLimitCounter.NextValue();
            sharedLimitBytes += sharedLimitCounter.NextValue();
        }

        var usedBytes = dedicatedUsedBytes + sharedUsedBytes;
        var totalBytes = dedicatedLimitBytes + sharedLimitBytes;
        var usedMb = usedBytes > 0 ? usedBytes / 1024d / 1024d : null;
        var totalMb = totalBytes > 0 ? totalBytes / 1024d / 1024d : null;
        return (usedMb, totalMb);
    }
    catch
    {
        return (null, null);
    }
}

static double? ReadGpuVramTotalMb()
{
    try
    {
        using var searcher = new ManagementObjectSearcher("SELECT AdapterRAM FROM Win32_VideoController");
        var values = searcher.Get().Cast<ManagementObject>()
            .Select(x => x["AdapterRAM"])
            .Where(x => x is not null)
            .Select(Convert.ToDouble)
            .Where(x => x > 0)
            .ToList();
        if (values.Count == 0)
        {
            return null;
        }

        return values.Max() / 1024d / 1024d;
    }
    catch
    {
        return null;
    }
}

static string? ReadGpuName()
{
    try
    {
        using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
        foreach (var row in searcher.Get().Cast<ManagementObject>())
        {
            var name = row["Name"]?.ToString();
            if (!string.IsNullOrWhiteSpace(name) && !name.Contains("Basic", StringComparison.OrdinalIgnoreCase))
            {
                return name.Trim();
            }
        }
    }
    catch
    {
    }

    return null;
}

static string? ReadGpuDriverVersion()
{
    try
    {
        using var searcher = new ManagementObjectSearcher("SELECT DriverVersion FROM Win32_VideoController");
        foreach (var row in searcher.Get().Cast<ManagementObject>())
        {
            var version = row["DriverVersion"]?.ToString();
            if (!string.IsNullOrWhiteSpace(version))
            {
                return version.Trim();
            }
        }
    }
    catch
    {
    }

    return null;
}

static (double? CoreClockMhz, double? MemoryClockMhz) ReadGpuClocksFromLhm(Computer? monitor)
{
    if (monitor is null)
    {
        return (null, null);
    }

    try
    {
        var coreClocks = new List<float>();
        var memClocks = new List<float>();
        foreach (var hw in monitor.Hardware.Where(h => h.HardwareType == HardwareType.GpuAmd || h.HardwareType == HardwareType.GpuNvidia || h.HardwareType == HardwareType.GpuIntel))
        {
            hw.Update();
            foreach (var sensor in hw.Sensors.Where(s => s.SensorType == SensorType.Clock && s.Value is not null))
            {
                if (sensor.Name.Contains("memory", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("mem", StringComparison.OrdinalIgnoreCase))
                {
                    memClocks.Add(sensor.Value!.Value);
                }
                else
                {
                    coreClocks.Add(sensor.Value!.Value);
                }
            }
        }

        return (
            coreClocks.Count > 0 ? coreClocks.Average() : null,
            memClocks.Count > 0 ? memClocks.Average() : null);
    }
    catch
    {
        return (null, null);
    }
}

static string? InferGpuVendor(string? name)
{
    if (string.IsNullOrWhiteSpace(name))
    {
        return null;
    }

    var n = name.ToLowerInvariant();
    if (n.Contains("nvidia")) return "NVIDIA";
    if (n.Contains("intel")) return "Intel";
    if (n.Contains("amd") || n.Contains("radeon")) return "AMD";
    return "Other";
}

static double? ReadSystemDriveTotalGb()
{
    try
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory);
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var d = new DriveInfo(root);
        if (!d.IsReady)
        {
            return null;
        }

        return Math.Round(d.TotalSize / 1024d / 1024d / 1024d);
    }
    catch
    {
        return null;
    }
}

static StorageInfo ReadPrimaryStorageInfo()
{
    try
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\');
        if (!string.IsNullOrWhiteSpace(root))
        {
            using var logicalToPartition = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{root}'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
            var partition = logicalToPartition.Get().Cast<ManagementObject>().FirstOrDefault();
            if (partition is not null)
            {
                var partitionPath = partition.Path?.Path;
                if (!string.IsNullOrWhiteSpace(partitionPath))
                {
                    using var partitionToDrive = new ManagementObjectSearcher(
                        $"ASSOCIATORS OF {{{partitionPath}}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");
                    var disk = partitionToDrive.Get().Cast<ManagementObject>().FirstOrDefault();
                    if (disk is not null)
                    {
                        return BuildStorageInfo(disk);
                    }
                }
            }
        }

        using var fallbackSearcher = new ManagementObjectSearcher("SELECT Model, MediaType, InterfaceType, PNPDeviceID FROM Win32_DiskDrive");
        var first = fallbackSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
        if (first is not null)
        {
            return BuildStorageInfo(first);
        }
    }
    catch
    {
    }

    return new StorageInfo(null, null, null);
}

static StorageInfo BuildStorageInfo(ManagementObject row)
{
    var modelRaw = row["Model"]?.ToString()?.Trim();
    var model = string.IsNullOrWhiteSpace(modelRaw) ? null : modelRaw;

    var mediaType = (row["MediaType"]?.ToString() ?? string.Empty).ToLowerInvariant();
    var interfaceTypeRaw = (row["InterfaceType"]?.ToString() ?? string.Empty).Trim();
    var interfaceType = string.IsNullOrWhiteSpace(interfaceTypeRaw) ? null : interfaceTypeRaw;
    var pnpId = (row["PNPDeviceID"]?.ToString() ?? string.Empty).ToLowerInvariant();
    var modelLower = (model ?? string.Empty).ToLowerInvariant();
    var merged = $"{modelLower} {mediaType} {interfaceTypeRaw.ToLowerInvariant()} {pnpId}";

    var type = merged.Contains("nvme")
        ? "NVMe"
        : merged.Contains("ssd") || merged.Contains("solid")
            ? "SSD"
            : merged.Contains("hdd") || merged.Contains("hard disk") || merged.Contains("sata")
                ? "HDD"
                : null;

    string? busType = null;
    if (merged.Contains("nvme"))
    {
        busType = "NVMe";
    }
    else if (!string.IsNullOrWhiteSpace(interfaceType))
    {
        busType = interfaceType.ToUpperInvariant();
    }

    return new StorageInfo(type, model, busType);
}

static string DetectDeviceClass()
{
    try
    {
        using var batterySearcher = new ManagementObjectSearcher("SELECT * FROM Win32_Battery");
        var hasBattery = batterySearcher.Get().Count > 0;
        if (hasBattery)
        {
            return "laptop";
        }

        using var enclosureSearcher = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
        foreach (var row in enclosureSearcher.Get().Cast<ManagementObject>())
        {
            if (row["ChassisTypes"] is not ushort[] arr)
            {
                continue;
            }

            if (arr.Any(x => x is 8 or 9 or 10 or 14))
            {
                return "laptop";
            }
        }
    }
    catch
    {
    }

    return "desktop";
}

static string? ReadMachineVendor()
{
    try
    {
        using var searcher = new ManagementObjectSearcher("SELECT Manufacturer FROM Win32_ComputerSystem");
        var row = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
        var vendor = row?["Manufacturer"]?.ToString()?.Trim();
        return string.IsNullOrWhiteSpace(vendor) ? null : vendor;
    }
    catch
    {
        return null;
    }
}

static string? ReadMachineModel()
{
    try
    {
        using var searcher = new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem");
        var row = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
        var model = row?["Model"]?.ToString()?.Trim();
        return string.IsNullOrWhiteSpace(model) ? null : model;
    }
    catch
    {
        return null;
    }
}

static double? ReadUptimeHours()
{
    try
    {
        return Math.Round(Environment.TickCount64 / 1000d / 3600d, 1);
    }
    catch
    {
        return null;
    }
}

static string? BucketByGb(double? gb)
{
    if (!gb.HasValue)
    {
        return null;
    }

    var val = gb.Value;
    if (val <= 8) return "<=8";
    if (val <= 16) return "9-16";
    if (val <= 32) return "17-32";
    if (val <= 64) return "33-64";
    if (val <= 128) return "65-128";
    return "129+";
}

sealed class TelemetrySampler : IDisposable
{
    private PerformanceCounter? _diskReadCounter;
    private PerformanceCounter? _diskWriteCounter;
    private DateTimeOffset _lastNetTs = DateTimeOffset.MinValue;
    private long _lastNetRecv;
    private long _lastNetSent;
    private bool _netInitialized;

    public MemoryTelemetry ReadMemoryTelemetry()
    {
        if (!NativeMethods.GetMemoryStatus(out var status))
        {
            return new MemoryTelemetry();
        }

        var total = status.ullTotalPhys;
        var avail = status.ullAvailPhys;
        var used = total > avail ? total - avail : 0;
        var util = total > 0 ? (used * 100.0 / total) : (double?)null;

        return new MemoryTelemetry
        {
            TotalBytes = total,
            UsedBytes = used,
            UtilPercent = util,
            TotalGb = total / 1024d / 1024d / 1024d,
            UsedGb = used / 1024d / 1024d / 1024d,
            AvailableGb = avail / 1024d / 1024d / 1024d,
        };
    }

    public DiskTelemetry ReadDiskTelemetry()
    {
        try
        {
            _diskReadCounter ??= new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", true);
            _diskWriteCounter ??= new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", true);

            var read = _diskReadCounter.NextValue();
            var write = _diskWriteCounter.NextValue();
            return new DiskTelemetry
            {
                ReadBps = read >= 0 ? read : null,
                WriteBps = write >= 0 ? write : null,
                TotalGb = GetSystemDriveTotalGb(),
                FreeGb = GetSystemDriveFreeGb(),
            };
        }
        catch
        {
            return new DiskTelemetry
            {
                TotalGb = GetSystemDriveTotalGb(),
                FreeGb = GetSystemDriveFreeGb(),
            };
        }
    }

    public NetworkTelemetry ReadNetworkTelemetry()
    {
        try
        {
            var upInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .Where(nic => nic.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
                .ToList();

            long recv = 0;
            long sent = 0;
            foreach (var nic in upInterfaces)
            {
                var stats = nic.GetIPv4Statistics();
                recv += stats.BytesReceived;
                sent += stats.BytesSent;
            }

            var active = upInterfaces.OrderByDescending(n => n.Speed).FirstOrDefault();
            var now = DateTimeOffset.UtcNow;
            if (!_netInitialized)
            {
                _netInitialized = true;
                _lastNetTs = now;
                _lastNetRecv = recv;
                _lastNetSent = sent;
                return new NetworkTelemetry
                {
                    RecvBps = null,
                    SentBps = null,
                    ActiveAdapterName = active?.Name,
                    LinkSpeedMbps = active is null ? null : active.Speed / 1_000_000d,
                };
            }

            var seconds = Math.Max((now - _lastNetTs).TotalSeconds, 0.001);
            var recvBps = Math.Max(0, recv - _lastNetRecv) / seconds;
            var sentBps = Math.Max(0, sent - _lastNetSent) / seconds;

            _lastNetTs = now;
            _lastNetRecv = recv;
            _lastNetSent = sent;

            return new NetworkTelemetry
            {
                RecvBps = recvBps,
                SentBps = sentBps,
                ActiveAdapterName = active?.Name,
                LinkSpeedMbps = active is null ? null : active.Speed / 1_000_000d,
            };
        }
        catch
        {
            return new NetworkTelemetry();
        }
    }

    public BatteryTelemetry ReadBatteryTelemetry()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery");
            var battery = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
            if (battery is null)
            {
                return new BatteryTelemetry();
            }

            var percent = battery["EstimatedChargeRemaining"] is null ? (double?)null : Convert.ToDouble(battery["EstimatedChargeRemaining"]);
            var status = battery["BatteryStatus"] is null ? (int?)null : Convert.ToInt32(battery["BatteryStatus"]);
            var isCharging = status is 2 or 6 or 7 or 8 or 9;

            return new BatteryTelemetry
            {
                Percent = percent,
                IsCharging = status.HasValue ? isCharging : null,
                DischargeW = null,
            };
        }
        catch
        {
            return new BatteryTelemetry();
        }
    }

    public void Dispose()
    {
        _diskReadCounter?.Dispose();
        _diskWriteCounter?.Dispose();
    }

    private static double? GetSystemDriveTotalGb()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            var d = new DriveInfo(root);
            if (!d.IsReady)
            {
                return null;
            }

            return Math.Round(d.TotalSize / 1024d / 1024d / 1024d);
        }
        catch
        {
            return null;
        }
    }

    private static double? GetSystemDriveFreeGb()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            var d = new DriveInfo(root);
            if (!d.IsReady)
            {
                return null;
            }

            return Math.Round(d.AvailableFreeSpace / 1024d / 1024d / 1024d);
        }
        catch
        {
            return null;
        }
    }
}

readonly record struct StorageInfo(string? Type, string? Model, string? BusType);

static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public static bool GetMemoryStatus(out MEMORYSTATUSEX status)
    {
        status = new MEMORYSTATUSEX();
        return GlobalMemoryStatusEx(ref status);
    }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
struct MEMORYSTATUSEX
{
    public uint dwLength;
    public uint dwMemoryLoad;
    public ulong ullTotalPhys;
    public ulong ullAvailPhys;
    public ulong ullTotalPageFile;
    public ulong ullAvailPageFile;
    public ulong ullTotalVirtual;
    public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual;

    public MEMORYSTATUSEX()
    {
        dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
    }
}

readonly record struct CpuCoreInfo(int? LogicalCores, int? PhysicalCores, string? Brand);
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
}
