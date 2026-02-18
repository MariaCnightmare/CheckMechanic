using System.Net;
using System.Text.Json;
using CheckMechanic.Shared;
using LibreHardwareMonitor.Hardware;

const string version = "1.0";
const int port = 17805;
using var instanceMutex = new Mutex(true, "Global\\CheckMechanic.SensorHelper.Singleton", out var createdNew);
if (!createdNew)
{
    return;
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, port);
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

var app = builder.Build();
Computer? monitor = null;
string? sensorInitError = null;
try
{
    monitor = new Computer
    {
        IsCpuEnabled = true,
        IsMotherboardEnabled = false,
        IsGpuEnabled = false,
        IsMemoryEnabled = false,
        IsStorageEnabled = false,
        IsNetworkEnabled = false,
    };
    monitor.Open();
}
catch (Exception)
{
    sensorInitError = "sensor backend init failed";
}

app.Lifetime.ApplicationStopping.Register(() =>
{
    monitor?.Close();
});

app.MapGet("/health", () => Results.Ok(new HealthResponse
{
    Ok = true,
    Version = version,
}));

app.MapGet("/v1/telemetry", () =>
{
    var cpu = ReadCpuTelemetry(monitor, sensorInitError);
    return Results.Ok(new TelemetryResponse
    {
        Ts = DateTimeOffset.Now,
        Cpu = cpu,
    });
});

try
{
    Console.WriteLine($"CheckMechanic.SensorHelper listening on http://127.0.0.1:{port}");
    app.Run();
}
catch (IOException ex) when (ex.Message.Contains("address", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("in use", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("port in use");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"helper fatal error: {ex.GetType().Name}");
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
