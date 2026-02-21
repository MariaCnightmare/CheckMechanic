using System.Text.Json;
using CheckMechanic.Shared;
using Xunit;

namespace CheckMechanic.Shared.Tests;

public class RankingProfileBuilderTests
{
    [Fact]
    public void RankingJson_DoesNotContainForbiddenKeys()
    {
        var profile = new SystemProfileDto
        {
            OsMajor = "11",
            OsBuildBucket = "226xx",
            CpuBrand = "Intel Core Ultra 7 258V",
            PhysicalCores = 8,
            LogicalCores = 8,
            MemoryTotalGbBucket = "17-32",
            GpuName = "Intel Arc Graphics",
            StoragePrimaryType = "NVMe",
            StorageTotalGbBucket = "513-1024",
            DeviceClass = "laptop",
            TempProvider = "coretemp",
        };

        var ranking = RankingProfileBuilder.FromSystemProfile(profile);
        var json = JsonSerializer.Serialize(ranking);

        Assert.False(RankingProfileBuilder.ContainsForbiddenKeysJson(json));
    }

    [Fact]
    public void ForbiddenKeyDetector_FindsHostnameKey()
    {
        const string json = "{\"os_major\":\"11\",\"hostname\":\"secret-host\"}";
        Assert.True(RankingProfileBuilder.ContainsForbiddenKeysJson(json));
    }
}
