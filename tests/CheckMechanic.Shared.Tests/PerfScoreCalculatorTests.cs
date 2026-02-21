using CheckMechanic.Shared;
using Xunit;

namespace CheckMechanic.Shared.Tests;

public class PerfScoreCalculatorTests
{
    [Fact]
    public void ScaleLog_RespectsBounds()
    {
        var low = 50 * 1024d;
        var high = 50 * 1024 * 1024d;

        Assert.Equal(0, PerfScoreCalculator.ScaleLog(low / 10, low, high), 5);
        Assert.Equal(0, PerfScoreCalculator.ScaleLog(low, low, high), 5);
        Assert.Equal(100, PerfScoreCalculator.ScaleLog(high, low, high), 5);
        Assert.Equal(100, PerfScoreCalculator.ScaleLog(high * 10, low, high), 5);
    }
}
