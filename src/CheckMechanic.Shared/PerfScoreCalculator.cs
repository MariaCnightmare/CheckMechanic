namespace CheckMechanic.Shared;

public sealed class PerfScoreResult
{
    public bool IsLocked { get; init; }
    public int Score { get; init; }
    public string Grade { get; init; } = "N/A";
    public double CpuHeadroom { get; init; }
    public double MemoryHeadroom { get; init; }
    public double? DiskHeadroom { get; init; }
    public double? NetworkHeadroom { get; init; }
    public double Stability { get; init; }
    public double TempPenalty { get; init; }
}

public static class PerfScoreCalculator
{
    public static PerfScoreResult Calculate(
        double? tempC,
        IReadOnlyList<double> cpuUtil60s,
        IReadOnlyList<double> memUtil60s,
        IReadOnlyList<double?> diskTotalBps60s,
        IReadOnlyList<double?> netTotalBps60s)
    {
        if (!tempC.HasValue)
        {
            return new PerfScoreResult { IsLocked = true, Grade = "LOCKED", Score = 0 };
        }

        var avgCpu = cpuUtil60s.Count > 0 ? cpuUtil60s.Average() : 0;
        var avgMem = memUtil60s.Count > 0 ? memUtil60s.Average() : 0;

        var cpuHeadroom = Clamp100(100 - avgCpu);
        var memHeadroom = Clamp100(100 - avgMem);

        var p95Disk = Percentile95(diskTotalBps60s);
        var p95Net = Percentile95(netTotalBps60s);

        double? diskHeadroom = null;
        if (p95Disk.HasValue)
        {
            diskHeadroom = 100 - ScaleLog(p95Disk.Value, 50 * 1024, 50 * 1024 * 1024);
        }

        double? netHeadroom = null;
        if (p95Net.HasValue)
        {
            netHeadroom = 100 - ScaleLog(p95Net.Value, 10 * 1024, 50 * 1024 * 1024);
        }

        var stability = CalculateStability(cpuUtil60s);
        var tempPenalty = CalculateTempPenalty(tempC.Value);

        var weighted = new List<(double value, double weight)>
        {
            (cpuHeadroom, 0.45),
            (memHeadroom, 0.35),
            (stability, 0.10),
        };
        if (diskHeadroom.HasValue) weighted.Add((diskHeadroom.Value, 0.05));
        if (netHeadroom.HasValue) weighted.Add((netHeadroom.Value, 0.05));

        var sumWeight = weighted.Sum(x => x.weight);
        var scoreRaw = sumWeight > 0 ? weighted.Sum(x => x.value * x.weight) / sumWeight : 0;
        var score = (int)Math.Round(Clamp100(scoreRaw - tempPenalty));

        return new PerfScoreResult
        {
            IsLocked = false,
            Score = score,
            Grade = ToGrade(score),
            CpuHeadroom = cpuHeadroom,
            MemoryHeadroom = memHeadroom,
            DiskHeadroom = diskHeadroom,
            NetworkHeadroom = netHeadroom,
            Stability = stability,
            TempPenalty = tempPenalty,
        };
    }

    public static double ScaleLog(double value, double low, double high)
    {
        if (high <= low)
        {
            return 100;
        }
        if (value <= low)
        {
            return 0;
        }
        if (value >= high)
        {
            return 100;
        }

        var lv = Math.Log10(value);
        var ll = Math.Log10(low);
        var lh = Math.Log10(high);
        return Clamp100(((lv - ll) / (lh - ll)) * 100.0);
    }

    private static double CalculateStability(IReadOnlyList<double> cpuUtil60s)
    {
        if (cpuUtil60s.Count <= 1)
        {
            return 100;
        }

        var mean = cpuUtil60s.Average();
        var variance = cpuUtil60s.Select(x => Math.Pow(x - mean, 2)).Average();
        var stddev = Math.Sqrt(variance);
        return Clamp100(100 - (stddev * 4.0));
    }

    private static double CalculateTempPenalty(double tempC)
    {
        if (tempC >= 85) return 15;
        if (tempC >= 75) return 8;
        if (tempC >= 65) return 3;
        return 0;
    }

    private static string ToGrade(int score)
    {
        if (score >= 80) return "A";
        if (score >= 60) return "B";
        if (score >= 40) return "C";
        if (score >= 20) return "D";
        return "E";
    }

    private static double Clamp100(double x) => Math.Clamp(x, 0, 100);

    private static double? Percentile95(IReadOnlyList<double?> values)
    {
        var v = values.Where(x => x.HasValue).Select(x => x!.Value).OrderBy(x => x).ToList();
        if (v.Count == 0)
        {
            return null;
        }

        var index = (int)Math.Ceiling(v.Count * 0.95) - 1;
        index = Math.Clamp(index, 0, v.Count - 1);
        return v[index];
    }
}
