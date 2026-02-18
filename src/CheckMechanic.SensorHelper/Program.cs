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

    if (TryReadCoreTempTemperature(out var coreTempValue, out var coreTempLabel, out var coreTempError))
    {
        return new CpuTelemetry
        {
            TempC = coreTempValue,
            UtilPercent = util,
            LogicalCores = coreInfo.LogicalCores,
            PhysicalCores = coreInfo.PhysicalCores,
            Brand = coreInfo.Brand,
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
        Label = null,
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
    return new GpuTelemetry
    {
        Name = name,
        UtilPercent = util,
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
    var memoryGb = memory.TotalBytes.HasValue ? Math.Round(memory.TotalBytes.Value / 1024d / 1024d / 1024d) : (double?)null;

    return new SystemProfileDto
    {
        OsMajor = major,
        OsBuildBucket = buildBucket,
        CpuBrand = cpu.Brand,
        PhysicalCores = cpu.PhysicalCores,
        LogicalCores = cpu.LogicalCores,
        MemoryTotalGbBucket = BucketByGb(memoryGb),
        GpuName = ReadGpuName(),
        StoragePrimaryType = ReadStoragePrimaryType(),
        StorageTotalGbBucket = BucketByGb(ReadSystemDriveTotalGb()),
        DeviceClass = DetectDeviceClass(),
        TempProvider = string.IsNullOrWhiteSpace(cpu.ProviderUsed) ? "unavailable" : cpu.ProviderUsed,
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
            values.AddRange(hw.Sensors
                .Where(s => s.SensorType == SensorType.Load && s.Value is not null)
                .Select(s => s.Value!.Value));
            foreach (var sub in hw.SubHardware)
            {
                sub.Update();
                values.AddRange(sub.Sensors
                    .Where(s => s.SensorType == SensorType.Load && s.Value is not null)
                    .Select(s => s.Value!.Value));
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

static string? ReadStoragePrimaryType()
{
    try
    {
        using var searcher = new ManagementObjectSearcher("SELECT Model, MediaType FROM Win32_DiskDrive");
        foreach (var row in searcher.Get().Cast<ManagementObject>())
        {
            var model = (row["Model"]?.ToString() ?? string.Empty).ToLowerInvariant();
            var mediaType = (row["MediaType"]?.ToString() ?? string.Empty).ToLowerInvariant();
            var merged = model + " " + mediaType;

            if (merged.Contains("nvme")) return "NVMe";
            if (merged.Contains("ssd") || merged.Contains("solid")) return "SSD";
            if (merged.Contains("hdd") || merged.Contains("sata") || merged.Contains("hard disk")) return "HDD";
        }
    }
    catch
    {
    }

    return null;
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
            var arr = row["ChassisTypes"] as ushort[];
            if (arr is null)
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
            };
        }
        catch
        {
            return new DiskTelemetry();
        }
    }

    public NetworkTelemetry ReadNetworkTelemetry()
    {
        try
        {
            long recv = 0;
            long sent = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var stats = nic.GetIPv4Statistics();
                recv += stats.BytesReceived;
                sent += stats.BytesSent;
            }

            var now = DateTimeOffset.UtcNow;
            if (!_netInitialized)
            {
                _netInitialized = true;
                _lastNetTs = now;
                _lastNetRecv = recv;
                _lastNetSent = sent;
                return new NetworkTelemetry { RecvBps = null, SentBps = null };
            }

            var seconds = Math.Max((now - _lastNetTs).TotalSeconds, 0.001);
            var recvBps = Math.Max(0, recv - _lastNetRecv) / seconds;
            var sentBps = Math.Max(0, sent - _lastNetSent) / seconds;

            _lastNetTs = now;
            _lastNetRecv = recv;
            _lastNetSent = sent;

            return new NetworkTelemetry { RecvBps = recvBps, SentBps = sentBps };
        }
        catch
        {
            return new NetworkTelemetry();
        }
    }

    public void Dispose()
    {
        _diskReadCounter?.Dispose();
        _diskWriteCounter?.Dispose();
    }
}

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
    public byte Fahrenheit;
    public byte DeltaToTjMax;
}
