using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace CheckMechanic.Shared;

public sealed class SystemProfileDto
{
    [JsonPropertyName("os_major")]
    public string? OsMajor { get; set; }

    [JsonPropertyName("os_build_bucket")]
    public string? OsBuildBucket { get; set; }

    [JsonPropertyName("cpu_brand")]
    public string? CpuBrand { get; set; }

    [JsonPropertyName("physical_cores")]
    public int? PhysicalCores { get; set; }

    [JsonPropertyName("logical_cores")]
    public int? LogicalCores { get; set; }

    [JsonPropertyName("memory_total_gb_bucket")]
    public string? MemoryTotalGbBucket { get; set; }

    [JsonPropertyName("gpu_name")]
    public string? GpuName { get; set; }

    [JsonPropertyName("storage_primary_type")]
    public string? StoragePrimaryType { get; set; }

    [JsonPropertyName("storage_total_gb_bucket")]
    public string? StorageTotalGbBucket { get; set; }

    [JsonPropertyName("device_class")]
    public string? DeviceClass { get; set; }

    [JsonPropertyName("temp_provider")]
    public string? TempProvider { get; set; }
}

public sealed class RankingProfileDto
{
    [JsonPropertyName("os_major")]
    public string? OsMajor { get; set; }

    [JsonPropertyName("cpu_family")]
    public string? CpuFamily { get; set; }

    [JsonPropertyName("cpu_cores")]
    public string? CpuCores { get; set; }

    [JsonPropertyName("ram_gb_bucket")]
    public string? RamGbBucket { get; set; }

    [JsonPropertyName("gpu_family")]
    public string? GpuFamily { get; set; }

    [JsonPropertyName("storage_type")]
    public string? StorageType { get; set; }

    [JsonPropertyName("device_class")]
    public string? DeviceClass { get; set; }

    [JsonPropertyName("temp_provider")]
    public string? TempProvider { get; set; }
}

public static class RankingProfileBuilder
{
    private static readonly Regex ForbiddenKeyRegex = new(
        "(hostname|username|user_name|user|mac|ssid|bssid|serial|uuid|smbios|productid|product_id|ip)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static RankingProfileDto FromSystemProfile(SystemProfileDto? profile)
    {
        if (profile is null)
        {
            return new RankingProfileDto();
        }

        return new RankingProfileDto
        {
            OsMajor = profile.OsMajor,
            CpuFamily = CategorizeCpuFamily(profile.CpuBrand),
            CpuCores = BucketCores(profile.LogicalCores ?? profile.PhysicalCores),
            RamGbBucket = profile.MemoryTotalGbBucket,
            GpuFamily = CategorizeGpuFamily(profile.GpuName),
            StorageType = profile.StoragePrimaryType,
            DeviceClass = profile.DeviceClass,
            TempProvider = profile.TempProvider,
        };
    }

    public static bool ContainsForbiddenKeysJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ContainsForbiddenKeysElement(doc.RootElement);
        }
        catch
        {
            return ForbiddenKeyRegex.IsMatch(json);
        }
    }

    private static bool ContainsForbiddenKeysElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in element.EnumerateObject())
            {
                if (ForbiddenKeyRegex.IsMatch(p.Name))
                {
                    return true;
                }
                if (ContainsForbiddenKeysElement(p.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in element.EnumerateArray())
            {
                if (ContainsForbiddenKeysElement(i))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static string CategorizeCpuFamily(string? brand)
    {
        var value = (brand ?? string.Empty).ToLowerInvariant();
        if (value.Contains("core ultra"))
        {
            return "Intel Core Ultra";
        }
        if (value.Contains("intel"))
        {
            return "Intel Core";
        }
        if (value.Contains("ryzen"))
        {
            return "AMD Ryzen";
        }
        if (value.Contains("amd"))
        {
            return "AMD";
        }
        return "Other";
    }

    private static string CategorizeGpuFamily(string? name)
    {
        var value = (name ?? string.Empty).ToLowerInvariant();
        if (value.Contains("rtx 40"))
        {
            return "NVIDIA RTX 40";
        }
        if (value.Contains("rtx"))
        {
            return "NVIDIA RTX";
        }
        if (value.Contains("arc"))
        {
            return "Intel Arc";
        }
        if (value.Contains("radeon"))
        {
            return "AMD Radeon";
        }
        if (value.Contains("intel"))
        {
            return "Intel iGPU";
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Unknown";
        }
        return "Other";
    }

    private static string BucketCores(int? cores)
    {
        if (!cores.HasValue || cores.Value <= 0)
        {
            return "unknown";
        }
        if (cores.Value <= 4) return "1-4";
        if (cores.Value <= 8) return "5-8";
        if (cores.Value <= 12) return "9-12";
        if (cores.Value <= 16) return "13-16";
        return "17+";
    }
}
