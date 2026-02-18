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
            IsMotherboardEnabled = false,
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
    if (monitor is null)
    {
        return new CpuTelemetry
        {
            TempC = null,
            Label = null,
            Error = sensorInitError ?? "sensor backend unavailable",
        };
    }

    try
    {
        IHardware? cpuHardware = monitor.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        if (cpuHardware is null)
        {
            return new CpuTelemetry
            {
                TempC = null,
                Label = null,
                Error = "cpu hardware not found",
            };
        }

        cpuHardware.Update();
        foreach (var sub in cpuHardware.SubHardware)
        {
            sub.Update();
        }

        var sensors = cpuHardware.Sensors
            .Concat(cpuHardware.SubHardware.SelectMany(sh => sh.Sensors))
            .Where(s => s.SensorType == SensorType.Temperature)
            .ToList();

        if (sensors.Count == 0)
        {
            return new CpuTelemetry
            {
                TempC = null,
                Label = null,
                Error = "temperature sensor not found",
            };
        }

        var package = sensors.FirstOrDefault(s => s.Name.Contains("package", StringComparison.OrdinalIgnoreCase));
        var picked = package ?? sensors.FirstOrDefault();

        return new CpuTelemetry
        {
            TempC = picked?.Value,
            Label = picked?.Name,
            Error = picked?.Value is null ? "temperature unavailable" : null,
        };
    }
    catch
    {
        return new CpuTelemetry
        {
            TempC = null,
            Label = null,
            Error = "failed to read cpu temperature",
        };
    }
}
