using System.Net;
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
        // Best-effort diagnostics only.
    }
}

Log("sensor helper starting");
Mutex? instanceMutex = null;
var ownsMutex = false;
try
{
    // Non-blocking singleton check to avoid hanging when another process holds the mutex.
    instanceMutex = new Mutex(false, "CheckMechanic.SensorHelper.Singleton", out var createdNew);
    if (createdNew)
    {
        ownsMutex = instanceMutex.WaitOne(0);
    }
    else
    {
        ownsMutex = instanceMutex.WaitOne(0);
    }

    if (!ownsMutex)
    {
        Log("sensor helper already running (mutex locked)");
        instanceMutex.Dispose();
        return;
    }
}
catch (Exception ex)
{
    Log($"mutex init failed: {ex.GetType().Name}: {ex.Message}");
    return;
}

WebApplicationBuilder builder;
try
{
    builder = WebApplication.CreateSlimBuilder(args);
}
catch (Exception ex)
{
    Log($"web builder init failed: {ex.GetType().Name}: {ex.Message}");
    return;
}
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, port);
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

WebApplication app;
try
{
    app = builder.Build();
}
catch (Exception ex)
{
    Log($"web app build failed: {ex.GetType().Name}: {ex.Message}");
    return;
}
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

app.Lifetime.ApplicationStopping.Register(() =>
{
    lock (monitorSync)
    {
        monitor?.Close();
    }
    if (ownsMutex)
    {
        try
        {
            instanceMutex?.ReleaseMutex();
        }
        catch
        {
            // ignore
        }
    }
    instanceMutex?.Dispose();
});

app.MapGet("/health", () => Results.Ok(new HealthResponse
{
    Ok = true,
    Version = version,
}));

app.MapGet("/v1/telemetry", () =>
{
    Computer? currentMonitor;
    string? currentInitError;
    lock (monitorSync)
    {
        currentMonitor = monitor;
        currentInitError = sensorInitError;
    }
    var cpu = ReadCpuTelemetry(currentMonitor, currentInitError);
    return Results.Ok(new TelemetryResponse
    {
        Ts = DateTimeOffset.Now,
        Cpu = cpu,
    });
});

try
{
    Log($"listening on http://127.0.0.1:{port}");
    app.Run();
}
catch (IOException ex) when (ex.Message.Contains("address", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("in use", StringComparison.OrdinalIgnoreCase))
{
    Log("port in use");
}
catch (Exception ex)
{
    Log($"helper fatal error: {ex.GetType().Name}: {ex.Message}");
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
    catch (Exception)
    {
        return new CpuTelemetry
        {
            TempC = null,
            Label = null,
            Error = "failed to read cpu temperature",
        };
    }
}
