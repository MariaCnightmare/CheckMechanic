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

    [JsonPropertyName("util_percent")]
    public double? UtilPercent { get; set; }

    [JsonPropertyName("logical_cores")]
    public int? LogicalCores { get; set; }

    [JsonPropertyName("physical_cores")]
    public int? PhysicalCores { get; set; }

    [JsonPropertyName("brand")]
    public string? Brand { get; set; }

    [JsonPropertyName("clock_mhz")]
    public double? ClockMhz { get; set; }

    [JsonPropertyName("power_w")]
    public double? PowerW { get; set; }

    [JsonPropertyName("temp_package_c")]
    public double? TempPackageC { get; set; }

    [JsonPropertyName("temp_core_max_c")]
    public double? TempCoreMaxC { get; set; }

    public string? Label { get; set; }

    public string Source { get; set; } = "LibreHardwareMonitorLib";

    public string? Error { get; set; }

    [JsonPropertyName("error_code")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("provider_used")]
    public string? ProviderUsed { get; set; }

    [JsonPropertyName("provider_errors")]
    public List<string> ProviderErrors { get; set; } = new();
}

public sealed class MemoryTelemetry
{
    [JsonPropertyName("total_bytes")]
    public ulong? TotalBytes { get; set; }

    [JsonPropertyName("used_bytes")]
    public ulong? UsedBytes { get; set; }

    [JsonPropertyName("util_percent")]
    public double? UtilPercent { get; set; }

    [JsonPropertyName("total_gb")]
    public double? TotalGb { get; set; }

    [JsonPropertyName("used_gb")]
    public double? UsedGb { get; set; }

    [JsonPropertyName("available_gb")]
    public double? AvailableGb { get; set; }
}

public sealed class DiskTelemetry
{
    [JsonPropertyName("read_bps")]
    public double? ReadBps { get; set; }

    [JsonPropertyName("write_bps")]
    public double? WriteBps { get; set; }

    [JsonPropertyName("total_gb")]
    public double? TotalGb { get; set; }

    [JsonPropertyName("free_gb")]
    public double? FreeGb { get; set; }
}

public sealed class NetworkTelemetry
{
    [JsonPropertyName("recv_bps")]
    public double? RecvBps { get; set; }

    [JsonPropertyName("sent_bps")]
    public double? SentBps { get; set; }

    [JsonPropertyName("active_adapter_name")]
    public string? ActiveAdapterName { get; set; }

    [JsonPropertyName("link_speed_mbps")]
    public double? LinkSpeedMbps { get; set; }
}

public sealed class GpuTelemetry
{
    public string? Name { get; set; }

    public string? Vendor { get; set; }

    [JsonPropertyName("driver_version")]
    public string? DriverVersion { get; set; }

    [JsonPropertyName("util_percent")]
    public double? UtilPercent { get; set; }

    [JsonPropertyName("vram_used_mb")]
    public double? VramUsedMb { get; set; }

    [JsonPropertyName("vram_total_mb")]
    public double? VramTotalMb { get; set; }

    [JsonPropertyName("temperature_c")]
    public double? TemperatureC { get; set; }

    [JsonPropertyName("core_clock_mhz")]
    public double? CoreClockMhz { get; set; }

    [JsonPropertyName("memory_clock_mhz")]
    public double? MemoryClockMhz { get; set; }
}

public sealed class BatteryTelemetry
{
    [JsonPropertyName("percent")]
    public double? Percent { get; set; }

    [JsonPropertyName("is_charging")]
    public bool? IsCharging { get; set; }

    [JsonPropertyName("discharge_w")]
    public double? DischargeW { get; set; }
}

public sealed class TelemetryResponse
{
    public DateTimeOffset Ts { get; set; }
    public CpuTelemetry Cpu { get; set; } = new();
    public MemoryTelemetry Memory { get; set; } = new();
    public DiskTelemetry Disk { get; set; } = new();
    public NetworkTelemetry Net { get; set; } = new();
    public GpuTelemetry Gpu { get; set; } = new();
    public BatteryTelemetry Battery { get; set; } = new();
}
