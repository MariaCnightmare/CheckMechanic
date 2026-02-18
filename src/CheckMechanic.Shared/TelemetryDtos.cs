using System.Text.Json.Serialization;

namespace CheckMechanic.Shared;

public sealed class HealthResponse
{
    public bool Ok { get; set; }
    public string Version { get; set; } = "1.0";
}

public sealed class CpuTelemetry
{
    [JsonPropertyName("temp_c")]
    public double? TempC { get; set; }

    public string? Label { get; set; }

    public string Source { get; set; } = "LibreHardwareMonitorLib";

    public string? Error { get; set; }
}

public sealed class TelemetryResponse
{
    public DateTimeOffset Ts { get; set; }
    public CpuTelemetry Cpu { get; set; } = new();
}
