using System.Net;
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
                    await WriteJsonAsync(ctx.Response, 200, new { sensors = Array.Empty<object>() });
                    return;
                }

                var sensors = CollectTemperatureSensors(currentMonitor)
                    .Select(s => new
                    {
                        hardware = s.HardwareName,
                        sensor = s.SensorName,
                        value = s.Value,
                        min = s.Min,
                        max = s.Max,
                    })
                    .ToList();
                await WriteJsonAsync(ctx.Response, 200, new { sensors });
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
    static double? EffectiveTemp(TempSensorSnapshot s)
    {
        if (s.Value is float v)
        {
            return v;
        }
        if (s.Max is float max)
        {
            return max;
        }
        if (s.Min is float min)
        {
            return min;
        }
        return null;
    }

    if (monitor is null)
    {
        var initCode = sensorInitError == "sensor backend initializing"
            ? "sensor_backend_initializing"
            : "sensor_backend_init_failed";
        return new CpuTelemetry
        {
            TempC = null,
            UtilPercent = null,
            Label = null,
            Error = sensorInitError ?? "sensor backend unavailable",
            ErrorCode = initCode,
        };
    }

    try
    {
        var sensors = CollectTemperatureSensors(monitor);
        if (sensors.Count == 0)
        {
            return new CpuTelemetry
            {
                TempC = null,
                UtilPercent = null,
                Label = null,
                Error = "temperature sensor not found",
                ErrorCode = "temperature_sensor_not_found",
            };
        }

        var valued = sensors.Where(s => EffectiveTemp(s) is not null).ToList();
        var valuedCpuLike = valued.FirstOrDefault(s => IsCpuLike(s.HardwareName, s.SensorName));
        var valuedPackage = valued.FirstOrDefault(s => s.SensorName.Contains("package", StringComparison.OrdinalIgnoreCase));
        var anyValued = valued.FirstOrDefault();

        var anyCpuLike = sensors.FirstOrDefault(s => IsCpuLike(s.HardwareName, s.SensorName));
        var anyPackage = sensors.FirstOrDefault(s => s.SensorName.Contains("package", StringComparison.OrdinalIgnoreCase));
        var anySensor = sensors.FirstOrDefault();
        var picked = valuedCpuLike ?? valuedPackage ?? anyValued ?? anyCpuLike ?? anyPackage ?? anySensor;
        var temp = picked is null ? null : EffectiveTemp(picked);
        var util = ReadCpuUtilization(monitor);

        return new CpuTelemetry
        {
            TempC = temp,
            UtilPercent = util,
            Label = picked is null ? null : $"{picked.HardwareName} / {picked.SensorName}",
            Error = picked is null || temp is null ? "temperature unavailable" : null,
            ErrorCode = picked is null || temp is null ? "sensor_values_unavailable" : null,
        };
    }
    catch
    {
        return new CpuTelemetry
        {
            TempC = null,
            UtilPercent = ReadCpuUtilization(monitor),
            Label = null,
            Error = "failed to read cpu temperature",
            ErrorCode = "sensor_read_failed",
        };
    }
}

static bool IsCpuLike(string hardwareName, string sensorName)
{
    var h = hardwareName.ToLowerInvariant();
    var s = sensorName.ToLowerInvariant();
    return h.Contains("cpu") || s.Contains("cpu") || s.Contains("package") || s.Contains("tctl") || s.Contains("tdie");
}

static List<TempSensorSnapshot> CollectTemperatureSensors(Computer monitor)
{
    var sensors = new List<TempSensorSnapshot>();
    foreach (var hw in monitor.Hardware)
    {
        WalkHardware(hw, sensors);
    }
    return sensors;
}

static void WalkHardware(IHardware hw, List<TempSensorSnapshot> sensors)
{
    hw.Update();
    foreach (var s in hw.Sensors.Where(x => x.SensorType == SensorType.Temperature))
    {
        sensors.Add(new TempSensorSnapshot(hw.Name, s.Name, s.Value, s.Min, s.Max));
    }
    foreach (var sub in hw.SubHardware)
    {
        WalkHardware(sub, sensors);
    }
}

record TempSensorSnapshot(string HardwareName, string SensorName, float? Value, float? Min, float? Max);

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
