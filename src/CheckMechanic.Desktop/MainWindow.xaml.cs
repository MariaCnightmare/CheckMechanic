using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CheckMechanic.Shared;
using Microsoft.Win32;

namespace CheckMechanic.Desktop;

public partial class MainWindow : Window
{
    private const string HelperBaseUrl = "http://127.0.0.1:17805";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(1.5) };
    private readonly DispatcherTimer _timer;
    private readonly Queue<double?> _tempHistory = new();
    private readonly Queue<double?> _cpuHistory = new();
    private readonly Queue<double?> _memHistory = new();
    private readonly Queue<double?> _diskHistory = new();
    private readonly Queue<double?> _netHistory = new();
    private readonly List<int> _localScoreHistory = new();

    private string _lastTelemetryJson = "{}";
    private string _lastProfileJson = "{}";
    private int _pollCount;
    private bool _optInConsent;
    private int _latestPerfScore;
    private string _latestPerfGrade = "N/A";

    public string CpuTemperatureText { get; set; } = "未取得";
    public string CpuKpiText { get; set; } = "--";
    public string CpuUtilizationText { get; set; } = "CPU使用率: 未取得";
    public string CpuAvgText { get; set; } = "avg(60s): --";
    public string MemoryText { get; set; } = "未取得";
    public string MemoryKpiText { get; set; } = "--";
    public string MemorySubText { get; set; } = "--";
    public string DiskText { get; set; } = "未取得";
    public string NetText { get; set; } = "未取得";
    public string PerfScoreText { get; set; } = "-- (N/A)";
    public string PerfSummaryText { get; set; } = "総合状態: --";
    public string StatusText { get; set; } = "⚠ 起動中";
    public string StatusBadgeText { get; set; } = "STARTING";
    public Brush StatusBadgeBackground { get; set; } = new SolidColorBrush(Color.FromRgb(24, 40, 56));
    public Brush StatusBadgeBorder { get; set; } = new SolidColorBrush(Color.FromRgb(39, 70, 91));
    public string RestrictionText { get; set; } = "必須要件未達: 温度取得が必要です。";
    public string SetupInstructionsText { get; set; } = "Core Temp を起動し、Options -> Settings -> Advanced -> Enable Global Shared Memory (SNMP) を ON にして「再チェック」を実行してください。";
    public string MainFeatureText { get; set; } = "制限モード: 主要機能は利用できません。診断情報を確認してください。";
    public string DiagnosticSensorsText { get; set; } = "(未取得)";
    public string ProfileText { get; set; } = "(未取得)";
    public string RankingProfileText { get; set; } = "Opt-in をONにするとカテゴリを生成します。";
    public string TempChartData { get; set; } = string.Empty;
    public string CpuChartData { get; set; } = string.Empty;
    public string MemChartData { get; set; } = string.Empty;
    public string TempRingArcData { get; set; } = string.Empty;
    public string TempRingCenterText { get; set; } = "--";
    public string TempLatestText { get; set; } = "--";
    public string TempMin60Text { get; set; } = "--";
    public string TempMax60Text { get; set; } = "--";
    public string TempSourceText { get; set; } = "source: unavailable";
    public string TempStatsText { get; set; } = "Latest -- / Min -- / Max --";
    public string CpuLatestText { get; set; } = "--";
    public string CpuAvg60Text { get; set; } = "--";
    public string MemLatestText { get; set; } = "--";
    public string MemoryAvailableText { get; set; } = "--";
    public double CpuUtilizationValue { get; set; }
    public double MemoryUtilizationValue { get; set; }
    public double ScoreCpuValue { get; set; }
    public double ScoreMemValue { get; set; }
    public double ScoreDiskValue { get; set; }
    public double ScoreNetValue { get; set; }
    public double ScoreTempValue { get; set; }
    public string ScoreCpuText { get; set; } = "--";
    public string ScoreMemText { get; set; } = "--";
    public string ScoreDiskText { get; set; } = "--";
    public string ScoreNetText { get; set; } = "--";
    public string ScoreTempText { get; set; } = "--";
    public Visibility RestrictionVisibility { get; set; } = Visibility.Visible;
    public string DiagnosticsSummaryText { get; set; } = "errors:0 / provider:- / updated:-";
    public string LocalRankingText { get; set; } = "ローカル履歴: N/A";

    public string ProfileOsMajor { get; set; } = "-";
    public string ProfileOsBuildBucket { get; set; } = "-";
    public string ProfileOsText { get; set; } = "-";
    public string ProfileCpuBrand { get; set; } = "-";
    public string ProfileCoreText { get; set; } = "-";
    public string ProfileMemoryBucket { get; set; } = "-";
    public string ProfileGpuName { get; set; } = "-";
    public string ProfileStorageType { get; set; } = "-";
    public string ProfileStorageBucket { get; set; } = "-";
    public string ProfileStorageText { get; set; } = "-";
    public string ProfileDeviceClass { get; set; } = "-";
    public string ProfileTempProvider { get; set; } = "-";
    public bool CloseCoreTempOnExitConsent { get; set; }
    public ObservableCollection<string> Logs { get; } = new();

    public bool OptInConsent
    {
        get => _optInConsent;
        set
        {
            _optInConsent = value;
            UpdateRankingPreview();
            UpdateLocalRankingText();
            RefreshBindings();
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += async (_, _) => await PollAllAsync();

        Loaded += async (_, _) => await InitializeAsync();
        Closing += MainWindow_OnClosing;
    }

    private async Task InitializeAsync()
    {
        LoadLocalScoreHistory();
        await EnsureHelperAvailableAsync();
        await PollAllAsync(forceProfile: true);
        await RefreshDiagnosticsAsync();
        _timer.Start();
    }

    private async Task PollAllAsync(bool forceProfile = false)
    {
        await PollTelemetryAsync();
        _pollCount++;
        if (forceProfile || _pollCount % 10 == 0)
        {
            await RefreshProfileAsync();
        }
    }

    private async Task EnsureHelperAvailableAsync()
    {
        if (await CheckHealthAsync())
        {
            SetStatus("✅ OK (helper connected)");
            return;
        }

        if (await IsPortOpenAsync())
        {
            SetStatus("❌ port in use (17805)");
            AddLog("port 17805 is already in use by another process");
            return;
        }

        SetStatus("⚠ SensorHelper 起動中");
        if (!TryStartHelper())
        {
            SetStatus("❌ SensorHelper 起動失敗");
            return;
        }

        for (var i = 0; i < 8; i++)
        {
            await Task.Delay(500);
            if (await CheckHealthAsync())
            {
                SetStatus("✅ OK (helper started)");
                return;
            }
        }

        if (await IsPortOpenAsync())
        {
            SetStatus("❌ port in use (17805)");
            AddLog("helper failed to bind: port 17805 may be in use");
        }
        else
        {
            SetStatus("❌ SensorHelper 応答なし");
        }
    }

    private async Task<bool> CheckHealthAsync()
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{HelperBaseUrl}/health");
            if (!response.IsSuccessStatusCode)
            {
                AddLog($"health HTTP {(int)response.StatusCode}");
                return false;
            }

            var json = await response.Content.ReadAsStringAsync();
            var payload = JsonSerializer.Deserialize<HealthResponse>(json, JsonOptions);
            return payload?.Ok == true;
        }
        catch
        {
            return false;
        }
    }

    private async Task PollTelemetryAsync()
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{HelperBaseUrl}/v1/telemetry");
            if (!response.IsSuccessStatusCode)
            {
                SetStatus($"❌ Telemetry HTTP {(int)response.StatusCode}");
                CpuTemperatureText = "未取得";
                SetRestrictedMode(true, "必須要件未達: 温度取得が必要です。", "Core Temp を起動した状態で再チェックしてください。");
                AddLog($"telemetry HTTP {(int)response.StatusCode}");
                RefreshBindings();
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            _lastTelemetryJson = json;
            var payload = JsonSerializer.Deserialize<TelemetryResponse>(json, JsonOptions);
            if (payload is null)
            {
                SetStatus("❌ Telemetry parse failed");
                CpuTemperatureText = "未取得";
                SetRestrictedMode(true, "必須要件未達: 温度取得が必要です。", "Core Temp を起動した状態で再チェックしてください。");
                AddLog("telemetry parse failed");
                RefreshBindings();
                return;
            }

            CpuUtilizationText = payload.Cpu.UtilPercent is double utilVal ? $"CPU使用率: {utilVal:F1}%" : "CPU使用率: 未取得";
            CpuUtilizationValue = payload.Cpu.UtilPercent is double cpuVal ? Math.Clamp(cpuVal, 0, 100) : 0;
            CpuKpiText = payload.Cpu.UtilPercent is double cpuNow ? $"{cpuNow:F1}%" : "--";
            MemoryText = BuildMemoryText(payload.Memory);
            MemoryUtilizationValue = payload.Memory.UtilPercent is double memVal ? Math.Clamp(memVal, 0, 100) : 0;
            MemoryAvailableText = BuildMemoryAvailableText(payload.Memory);
            MemoryKpiText = BuildMemoryKpiText(payload.Memory);
            MemorySubText = payload.Memory.UtilPercent is double memNow ? $"({memNow:F1}%) / available {MemoryAvailableText}" : $"available {MemoryAvailableText}";
            DiskText = $"R {FormatRate(payload.Disk.ReadBps)} / W {FormatRate(payload.Disk.WriteBps)}";
            NetText = $"↓ {FormatRate(payload.Net.RecvBps)} / ↑ {FormatRate(payload.Net.SentBps)}";
            CpuLatestText = payload.Cpu.UtilPercent is double cu ? $"{cu:F1}%" : "--";
            MemLatestText = payload.Memory.UtilPercent is double mu ? $"{mu:F1}%" : "--";
            TempSourceText = $"source: {payload.Cpu.ProviderUsed ?? payload.Cpu.Source ?? "unavailable"}";

            if (payload.Cpu.TempC is double temp)
            {
                CpuTemperatureText = $"{temp:F1} °C";
                TempLatestText = $"{temp:F1}°C";
                TempRingCenterText = $"{temp:F0}";
                TempRingArcData = BuildRingArcPath(temp, 30, 100, 104, 12);
                SetStatus("✅ OK");
                SetRestrictedMode(false, string.Empty, string.Empty);
                AddLog($"temp={temp:F1}C util={payload.Cpu.UtilPercent?.ToString("F1") ?? "n/a"} provider={payload.Cpu.ProviderUsed ?? "unknown"} label={payload.Cpu.Label}");
            }
            else
            {
                CpuTemperatureText = "未取得";
                TempLatestText = "Restricted";
                TempRingCenterText = "R";
                TempRingArcData = string.Empty;
                TempSourceText = "source: unavailable";
                SetStatus("❌ 必須要件未達（温度取得不可）");
                SetRestrictedMode(
                    true,
                    "必須要件未達: 温度取得が必要です。管理者で再試行してください。",
                    BuildSetupInstructions(payload.Cpu.ProviderErrors));
                var providerErrors = payload.Cpu.ProviderErrors?.Count > 0 ? string.Join(",", payload.Cpu.ProviderErrors) : "none";
                AddLog($"temperature unavailable: code={payload.Cpu.ErrorCode ?? "unknown"} detail={payload.Cpu.Error ?? "unknown"} util={payload.Cpu.UtilPercent?.ToString("F1") ?? "n/a"} provider_errors={providerErrors}");
            }

            double? diskTotal = (payload.Disk.ReadBps.HasValue || payload.Disk.WriteBps.HasValue)
                ? payload.Disk.ReadBps.GetValueOrDefault() + payload.Disk.WriteBps.GetValueOrDefault()
                : null;
            double? netTotal = (payload.Net.RecvBps.HasValue || payload.Net.SentBps.HasValue)
                ? payload.Net.RecvBps.GetValueOrDefault() + payload.Net.SentBps.GetValueOrDefault()
                : null;
            AppendHistory(
                payload.Cpu.TempC,
                payload.Cpu.UtilPercent,
                payload.Memory.UtilPercent,
                diskTotal,
                netTotal);
            UpdateScore(payload);
            RefreshChartData();
            TempStatsText = $"Latest {TempLatestText} / Min {TempMin60Text} / Max {TempMax60Text}";
            CpuAvgText = $"avg(60s): {CpuAvg60Text}";
            DiagnosticsSummaryText = BuildDiagnosticsSummary(payload);
            RefreshBindings();
        }
        catch (HttpRequestException ex)
        {
            CpuTemperatureText = "未取得";
            TempLatestText = "--";
            TempRingCenterText = "R";
            TempRingArcData = string.Empty;
            TempSourceText = "source: unavailable";
            SetStatus("❌ SensorHelper 未接続");
            SetRestrictedMode(true, "必須要件未達: 温度取得が必要です。", "Core Temp を起動した状態で再チェックしてください。");
            AddLog(SanitizeError(ex.Message));
            RefreshBindings();
        }
        catch
        {
            CpuTemperatureText = "未取得";
            TempLatestText = "--";
            TempRingCenterText = "R";
            TempRingArcData = string.Empty;
            TempSourceText = "source: unavailable";
            SetStatus("❌ 取得失敗");
            SetRestrictedMode(true, "必須要件未達: 温度取得が必要です。", "Core Temp を起動した状態で再チェックしてください。");
            AddLog("telemetry request failed");
            RefreshBindings();
        }
    }

    private async Task RefreshProfileAsync()
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{HelperBaseUrl}/v1/profile");
            if (!response.IsSuccessStatusCode)
            {
                ProfileText = $"{{\"error\":\"HTTP {(int)response.StatusCode}\"}}";
                UpdateRankingPreview();
                return;
            }

            var raw = await response.Content.ReadAsStringAsync();
            _lastProfileJson = raw;
            ProfileText = PrettyJson(raw);
            var profile = JsonSerializer.Deserialize<SystemProfileDto>(raw, JsonOptions) ?? new SystemProfileDto();
            ProfileOsMajor = profile.OsMajor ?? "-";
            ProfileOsBuildBucket = profile.OsBuildBucket ?? "-";
            ProfileOsText = $"{ProfileOsMajor} / {ProfileOsBuildBucket}";
            ProfileCpuBrand = profile.CpuBrand ?? "-";
            var logical = profile.LogicalCores?.ToString() ?? "-";
            var physical = profile.PhysicalCores?.ToString() ?? "-";
            ProfileCoreText = $"{physical}C / {logical}T";
            ProfileMemoryBucket = profile.MemoryTotalGbBucket ?? "-";
            ProfileGpuName = profile.GpuName ?? "-";
            ProfileStorageType = profile.StoragePrimaryType ?? "-";
            ProfileStorageBucket = profile.StorageTotalGbBucket ?? "-";
            ProfileStorageText = $"{ProfileStorageType} / {ProfileStorageBucket}";
            ProfileDeviceClass = profile.DeviceClass ?? "-";
            ProfileTempProvider = profile.TempProvider ?? "-";
            UpdateRankingPreview();
        }
        catch
        {
            ProfileText = "{\"error\":\"profile_unavailable\"}";
            ProfileOsMajor = "-";
            ProfileOsBuildBucket = "-";
            ProfileOsText = "-";
            ProfileCpuBrand = "-";
            ProfileCoreText = "-";
            ProfileMemoryBucket = "-";
            ProfileGpuName = "-";
            ProfileStorageType = "-";
            ProfileStorageBucket = "-";
            ProfileStorageText = "-";
            ProfileDeviceClass = "-";
            ProfileTempProvider = "-";
            UpdateRankingPreview();
        }
    }

    private void UpdateRankingPreview()
    {
        if (!OptInConsent)
        {
            RankingProfileText = "Opt-in をONにするとカテゴリを生成します。";
            return;
        }

        try
        {
            var profile = JsonSerializer.Deserialize<SystemProfileDto>(_lastProfileJson, JsonOptions) ?? new SystemProfileDto();
            var ranking = RankingProfileBuilder.FromSystemProfile(profile);
            var json = JsonSerializer.Serialize(ranking, JsonOptions);
            if (RankingProfileBuilder.ContainsForbiddenKeysJson(json))
            {
                RankingProfileText = "{\"error\":\"forbidden_keys_detected\"}";
                AddLog("ranking profile blocked: forbidden key detected");
                return;
            }

            RankingProfileText = json;
        }
        catch
        {
            RankingProfileText = "{\"error\":\"ranking_profile_generate_failed\"}";
        }
    }

    private void UpdateScore(TelemetryResponse payload)
    {
        var cpuHistory = _cpuHistory.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        var memHistory = _memHistory.Where(x => x.HasValue).Select(x => x!.Value).ToList();

        var result = PerfScoreCalculator.Calculate(
            payload.Cpu.TempC,
            cpuHistory,
            memHistory,
            _diskHistory.ToList(),
            _netHistory.ToList());

        if (result.IsLocked)
        {
            PerfScoreText = "N/A (LOCKED)";
            PerfSummaryText = "温度要件未達のため評価不可";
            ScoreCpuValue = 0;
            ScoreMemValue = 0;
            ScoreDiskValue = 0;
            ScoreNetValue = 0;
            ScoreTempValue = 0;
            ScoreCpuText = "--";
            ScoreMemText = "--";
            ScoreDiskText = "--";
            ScoreNetText = "--";
            ScoreTempText = "--";
            return;
        }

        _latestPerfScore = result.Score;
        _latestPerfGrade = result.Grade;
        PerfScoreText = $"{result.Score} ({result.Grade})";
        PerfSummaryText = $"penalty {result.TempPenalty:F0} / stability {result.Stability:F0}";

        ScoreCpuValue = Math.Round(result.CpuHeadroom, 1);
        ScoreMemValue = Math.Round(result.MemoryHeadroom, 1);
        ScoreDiskValue = Math.Round(result.DiskHeadroom ?? 0, 1);
        ScoreNetValue = Math.Round(result.NetworkHeadroom ?? 0, 1);
        ScoreTempValue = Math.Round(100 - result.TempPenalty * 5, 1);
        ScoreCpuText = $"{ScoreCpuValue:F0}";
        ScoreMemText = $"{ScoreMemValue:F0}";
        ScoreDiskText = result.DiskHeadroom.HasValue ? $"{ScoreDiskValue:F0}" : "N/A";
        ScoreNetText = result.NetworkHeadroom.HasValue ? $"{ScoreNetValue:F0}" : "N/A";
        ScoreTempText = $"{ScoreTempValue:F0}";

        PersistLocalScoreIfOptIn(result.Score);
        UpdateLocalRankingText();
    }

    private void AppendHistory(double? temp, double? cpu, double? mem, double? diskTotalBps, double? netTotalBps)
    {
        AppendWithLimit(_tempHistory, temp, 60);
        AppendWithLimit(_cpuHistory, cpu, 60);
        AppendWithLimit(_memHistory, mem, 60);
        AppendWithLimit(_diskHistory, diskTotalBps, 60);
        AppendWithLimit(_netHistory, netTotalBps, 60);
    }

    private static void AppendWithLimit(Queue<double?> queue, double? value, int max)
    {
        queue.Enqueue(value);
        while (queue.Count > max)
        {
            queue.Dequeue();
        }
    }

    private void RefreshChartData()
    {
        TempChartData = BuildSparklinePath(_tempHistory, 780, 190, 30, 100);
        CpuChartData = BuildSparklinePath(_cpuHistory, 780, 190, 0, 100);
        MemChartData = BuildSparklinePath(_memHistory, 780, 190, 0, 100);

        var tempValues = _tempHistory.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        TempMin60Text = tempValues.Count > 0 ? $"{tempValues.Min():F1}°C" : "--";
        TempMax60Text = tempValues.Count > 0 ? $"{tempValues.Max():F1}°C" : "--";

        var cpuValues = _cpuHistory.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        CpuAvg60Text = cpuValues.Count > 0 ? $"{cpuValues.Average():F1}%" : "--";
    }

    private static string BuildSparklinePath(IEnumerable<double?> values, double width, double height, double minY, double maxY)
    {
        var list = values.ToList();
        if (list.Count < 2)
        {
            return string.Empty;
        }

        var stepX = width / Math.Max(list.Count - 1, 1);
        var sb = new System.Text.StringBuilder();
        var started = false;

        for (var i = 0; i < list.Count; i++)
        {
            var v = list[i];
            if (!v.HasValue)
            {
                continue;
            }

            var x = i * stepX;
            var normalized = maxY > minY ? Math.Clamp((v.Value - minY) / (maxY - minY), 0, 1) : 0;
            var y = height - (normalized * height);
            if (!started)
            {
                sb.Append($"M {x:F1},{y:F1} ");
                started = true;
            }
            else
            {
                sb.Append($"L {x:F1},{y:F1} ");
            }
        }

        return sb.ToString();
    }

    private static string BuildRingArcPath(double value, double min, double max, double size, double stroke)
    {
        var progress = max > min ? Math.Clamp((value - min) / (max - min), 0, 1) : 0;
        if (progress <= 0)
        {
            return string.Empty;
        }

        var radius = (size / 2.0) - (stroke / 2.0);
        var center = size / 2.0;
        var startAngle = -90.0;
        var endAngle = startAngle + (progress * 360.0);
        var start = PolarToCartesian(center, center, radius, startAngle);
        var end = PolarToCartesian(center, center, radius, endAngle);
        var isLargeArc = progress > 0.5 ? 1 : 0;
        return $"M {start.X:F2},{start.Y:F2} A {radius:F2},{radius:F2} 0 {isLargeArc} 1 {end.X:F2},{end.Y:F2}";
    }

    private static Point PolarToCartesian(double centerX, double centerY, double radius, double angleDegrees)
    {
        var angleRadians = angleDegrees * Math.PI / 180.0;
        return new Point(
            centerX + (radius * Math.Cos(angleRadians)),
            centerY + (radius * Math.Sin(angleRadians)));
    }

    private static string BuildMemoryText(MemoryTelemetry memory)
    {
        if (!memory.TotalBytes.HasValue || !memory.UsedBytes.HasValue)
        {
            return "未取得";
        }

        var used = memory.UsedBytes.Value / 1024d / 1024d / 1024d;
        var total = memory.TotalBytes.Value / 1024d / 1024d / 1024d;
        var util = memory.UtilPercent.HasValue ? $" ({memory.UtilPercent.Value:F1}%)" : string.Empty;
        return $"{used:F1} / {total:F1} GB{util}";
    }

    private static string BuildMemoryAvailableText(MemoryTelemetry memory)
    {
        if (!memory.TotalBytes.HasValue || !memory.UsedBytes.HasValue)
        {
            return "--";
        }

        var available = (memory.TotalBytes.Value - memory.UsedBytes.Value) / 1024d / 1024d / 1024d;
        return $"{available:F1} GB";
    }

    private static string BuildMemoryKpiText(MemoryTelemetry memory)
    {
        if (!memory.TotalBytes.HasValue || !memory.UsedBytes.HasValue)
        {
            return "--";
        }

        var used = memory.UsedBytes.Value / 1024d / 1024d / 1024d;
        var total = memory.TotalBytes.Value / 1024d / 1024d / 1024d;
        return $"{used:F1} / {total:F1} GB";
    }

    private static string BuildDiagnosticsSummary(TelemetryResponse payload)
    {
        var provider = payload.Cpu.ProviderUsed ?? "none";
        var errors = payload.Cpu.ProviderErrors?.Count ?? 0;
        return $"errors:{errors} / provider:{provider} / updated:{DateTime.Now:HH:mm:ss}";
    }

    private static string GetHistoryFilePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".checkmechanic");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "perf_history.json");
    }

    private void LoadLocalScoreHistory()
    {
        try
        {
            var path = GetHistoryFilePath();
            if (!File.Exists(path))
            {
                return;
            }

            var json = File.ReadAllText(path);
            var history = JsonSerializer.Deserialize<List<int>>(json, JsonOptions) ?? new List<int>();
            _localScoreHistory.Clear();
            _localScoreHistory.AddRange(history.Where(x => x is >= 0 and <= 100).TakeLast(30));
            UpdateLocalRankingText();
        }
        catch
        {
            _localScoreHistory.Clear();
            LocalRankingText = "ローカル履歴: 読み込み失敗";
        }
    }

    private void PersistLocalScoreIfOptIn(int score)
    {
        if (!OptInConsent)
        {
            return;
        }

        try
        {
            _localScoreHistory.Add(score);
            while (_localScoreHistory.Count > 30)
            {
                _localScoreHistory.RemoveAt(0);
            }

            var path = GetHistoryFilePath();
            File.WriteAllText(path, JsonSerializer.Serialize(_localScoreHistory, JsonOptions));
        }
        catch
        {
            AddLog("local score history write failed");
        }
    }

    private void UpdateLocalRankingText()
    {
        if (!OptInConsent)
        {
            LocalRankingText = "ローカル履歴: Opt-in OFF";
            return;
        }

        if (_localScoreHistory.Count == 0)
        {
            LocalRankingText = "ローカル履歴: N/A (Opt-in時に保存)";
            return;
        }

        var avg = _localScoreHistory.Average();
        var best = _localScoreHistory.Max();
        var worst = _localScoreHistory.Min();
        var delta = _latestPerfScore - avg;
        LocalRankingText = $"you:{_latestPerfScore}({_latestPerfGrade}) / avg:{avg:F1} ({delta:+0.0;-0.0;0}) / best:{best} / worst:{worst}";
    }

    private static string FormatRate(double? bps)
    {
        if (!bps.HasValue)
        {
            return "n/a";
        }

        var value = bps.Value;
        if (value >= 1024 * 1024 * 1024) return $"{value / 1024 / 1024 / 1024:F2} GB/s";
        if (value >= 1024 * 1024) return $"{value / 1024 / 1024:F1} MB/s";
        if (value >= 1024) return $"{value / 1024:F1} KB/s";
        return $"{value:F0} B/s";
    }

    private bool TryStartHelper(bool runAsAdmin = false)
    {
        var startInfo = ResolveHelperStartInfo(runAsAdmin);
        if (startInfo is null)
        {
            AddLog("helper binary not found");
            return false;
        }

        try
        {
            Process.Start(startInfo);
            AddLog(runAsAdmin
                ? $"helper started as admin: {startInfo.FileName} {startInfo.Arguments}".Trim()
                : $"helper started: {startInfo.FileName} {startInfo.Arguments}".Trim());
            return true;
        }
        catch (Win32Exception ex) when (runAsAdmin && ex.NativeErrorCode == 1223)
        {
            SetStatus("⚠ 管理者昇格がキャンセルされました");
            AddLog("admin elevation canceled by user");
            return false;
        }
        catch
        {
            AddLog("helper start failed");
            return false;
        }
    }

    private static ProcessStartInfo? ResolveHelperStartInfo(bool runAsAdmin)
    {
        var baseDir = AppContext.BaseDirectory;
        var exeCandidates = new[]
        {
            Path.Combine(baseDir, "CheckMechanic.SensorHelper.exe"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "CheckMechanic.SensorHelper", "bin", "Debug", "net8.0-windows", "CheckMechanic.SensorHelper.exe")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "CheckMechanic.SensorHelper", "bin", "Release", "net8.0-windows", "CheckMechanic.SensorHelper.exe")),
        };

        var exePath = exeCandidates.FirstOrDefault(File.Exists);
        if (exePath is not null)
        {
            return new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = runAsAdmin,
                Verb = runAsAdmin ? "runas" : string.Empty,
                CreateNoWindow = !runAsAdmin,
                WorkingDirectory = Path.GetDirectoryName(exePath),
            };
        }

        var dllCandidates = new[]
        {
            Path.Combine(baseDir, "CheckMechanic.SensorHelper.dll"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "CheckMechanic.SensorHelper", "bin", "Debug", "net8.0-windows", "CheckMechanic.SensorHelper.dll")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "CheckMechanic.SensorHelper", "bin", "Release", "net8.0-windows", "CheckMechanic.SensorHelper.dll")),
        };
        var dllPath = dllCandidates.FirstOrDefault(File.Exists);
        if (dllPath is not null)
        {
            return new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{dllPath}\"",
                UseShellExecute = runAsAdmin,
                Verb = runAsAdmin ? "runas" : string.Empty,
                CreateNoWindow = !runAsAdmin,
                WorkingDirectory = Path.GetDirectoryName(dllPath),
            };
        }

        return null;
    }

    private async void ReconnectButton_OnClick(object sender, RoutedEventArgs e)
    {
        await EnsureHelperAvailableAsync();
    }

    private async void LaunchOrActivateCoreTempButton_OnClick(object sender, RoutedEventArgs e)
    {
        var proc = LaunchOrActivateCoreTemp();
        if (proc is null)
        {
            SetStatus("⚠ Core Temp を起動できませんでした");
            AddLog("core temp launch/activate failed");
            return;
        }

        AddLog($"core temp ready: pid={proc.Id}");
        SetStatus("⚠ Core Temp 起動/前面化済み。再チェックしてください。");
        await Task.Delay(700);
        await RefreshDiagnosticsAsync();
    }

    private async void RecheckButton_OnClick(object sender, RoutedEventArgs e)
    {
        await EnsureHelperAvailableAsync();
        await PollAllAsync(forceProfile: true);
        await RefreshDiagnosticsAsync();
    }

    private async void RestartHelperButton_OnClick(object sender, RoutedEventArgs e)
    {
        TryKillHelperProcesses();
        await Task.Delay(300);
        await EnsureHelperAvailableAsync();
        await PollAllAsync(forceProfile: true);
    }

    private async void RestartHelperAsAdminButton_OnClick(object sender, RoutedEventArgs e)
    {
        TryKillHelperProcesses();
        await Task.Delay(300);
        if (!TryStartHelper(runAsAdmin: true))
        {
            RefreshBindings();
            return;
        }

        await Task.Delay(1200);
        await EnsureHelperAvailableAsync();
        await PollAllAsync(forceProfile: true);
    }

    private async void RefreshDiagnosticsButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RefreshDiagnosticsAsync();
        RefreshBindings();
    }

    private async void RefreshProfileButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RefreshProfileAsync();
        RefreshBindings();
    }

    private void MoreActionsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.ContextMenu is null)
        {
            return;
        }

        fe.ContextMenu.PlacementTarget = fe;
        fe.ContextMenu.IsOpen = true;
    }

    private void ExportDiagnosticsButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                FileName = $"checkmechanic_diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.json",
                Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var payload = new
            {
                exported_at = DateTimeOffset.Now,
                status = StatusText,
                restriction = RestrictionText,
                cpu_temperature_text = CpuTemperatureText,
                cpu_utilization_text = CpuUtilizationText,
                memory_text = MemoryText,
                disk_text = DiskText,
                net_text = NetText,
                perf_score_text = PerfScoreText,
                telemetry_raw = TryParseJsonOrRaw(_lastTelemetryJson),
                profile_raw = TryParseJsonOrRaw(_lastProfileJson),
                ranking_profile_preview = TryParseJsonOrRaw(RankingProfileText),
                sensors_raw = TryParseJsonOrRaw(DiagnosticSensorsText),
                logs = Logs.ToList(),
            };
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(payload, JsonOptions));
            AddLog($"diagnostics exported: {dialog.FileName}");
        }
        catch
        {
            AddLog("diagnostics export failed");
        }

        RefreshBindings();
    }

    private void SetStatus(string text)
    {
        StatusText = text;
        if (text.Contains("✅", StringComparison.Ordinal))
        {
            StatusBadgeText = "OK";
            StatusBadgeBackground = new SolidColorBrush(Color.FromRgb(24, 58, 44));
            StatusBadgeBorder = new SolidColorBrush(Color.FromRgb(61, 132, 101));
        }
        else if (text.Contains("必須要件未達", StringComparison.Ordinal) || text.Contains("温度取得不可", StringComparison.Ordinal))
        {
            StatusBadgeText = "LOCKED";
            StatusBadgeBackground = new SolidColorBrush(Color.FromRgb(57, 30, 36));
            StatusBadgeBorder = new SolidColorBrush(Color.FromRgb(147, 78, 91));
        }
        else if (text.Contains("❌", StringComparison.Ordinal))
        {
            StatusBadgeText = "DEGRADED";
            StatusBadgeBackground = new SolidColorBrush(Color.FromRgb(60, 52, 26));
            StatusBadgeBorder = new SolidColorBrush(Color.FromRgb(156, 136, 71));
        }
        else
        {
            StatusBadgeText = "STARTING";
            StatusBadgeBackground = new SolidColorBrush(Color.FromRgb(48, 42, 24));
            StatusBadgeBorder = new SolidColorBrush(Color.FromRgb(141, 122, 68));
        }
        RefreshBindings();
    }

    private void SetRestrictedMode(bool restricted, string reason, string setupInstructions)
    {
        RestrictionText = restricted ? reason : string.Empty;
        SetupInstructionsText = restricted ? setupInstructions : string.Empty;
        RestrictionVisibility = restricted ? Visibility.Visible : Visibility.Collapsed;
        MainFeatureText = restricted
            ? "制限モード: 温度取得が確認できるまで主要画面は利用不可です。診断情報を確認してください。"
            : "通常モード: 温度取得を確認済みです。";
        if (restricted)
        {
            CpuTemperatureText = "利用不可（温度要件未達）";
        }
    }

    private void CopyLogsButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Logs.Count == 0)
            {
                Clipboard.SetText(string.Empty);
                AddLog("logs copied: empty");
                return;
            }

            var text = string.Join(Environment.NewLine, Logs);
            Clipboard.SetText(text);
            AddLog($"logs copied: {Logs.Count} lines");
        }
        catch (Exception ex)
        {
            AddLog($"log copy failed: {ex.GetType().Name}: {SanitizeError(ex.Message)}");
        }
    }

    private void AddLog(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        Logs.Insert(0, line);
        while (Logs.Count > 120)
        {
            Logs.RemoveAt(Logs.Count - 1);
        }
    }

    private static string SanitizeError(string message)
    {
        if (message.Contains("127.0.0.1") || message.Contains("localhost"))
        {
            return "sensor helper connection failed";
        }

        return "sensor helper request failed";
    }

    private async Task RefreshDiagnosticsAsync()
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{HelperBaseUrl}/v1/sensors");
            if (!response.IsSuccessStatusCode)
            {
                DiagnosticSensorsText = $"{{\"error\":\"HTTP {(int)response.StatusCode}\"}}";
                return;
            }

            var raw = await response.Content.ReadAsStringAsync();
            DiagnosticSensorsText = PrettyJson(raw);
        }
        catch
        {
            DiagnosticSensorsText = "{\"error\":\"diagnostics_unavailable\"}";
        }
    }

    private static object TryParseJsonOrRaw(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch
        {
            return raw;
        }
    }

    private static string PrettyJson(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(doc.RootElement, JsonOptions);
        }
        catch
        {
            return raw;
        }
    }

    private void TryKillHelperProcesses()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("CheckMechanic.SensorHelper"))
            {
                p.Kill(entireProcessTree: true);
            }
            AddLog("helper processes terminated");
        }
        catch
        {
            AddLog("helper terminate failed");
        }
    }

    private static string BuildSetupInstructions(IReadOnlyList<string>? providerErrors)
    {
        var list = providerErrors ?? Array.Empty<string>();
        if (list.Any(x => x.Contains("coretemp:TEMP_CORETEMP_SHM_NOT_FOUND", StringComparison.OrdinalIgnoreCase)))
        {
            return "Core Temp が未検出です。Core Temp を起動し、Options -> Settings -> Advanced -> Enable Global Shared Memory (SNMP) を ON にして「再チェック」を実行してください。";
        }

        if (list.Any(x => x.Contains("coretemp:TEMP_CORETEMP_VALUES_UNAVAILABLE", StringComparison.OrdinalIgnoreCase)))
        {
            return "Core Temp は検出されていますが温度値を取得できません。Options -> Settings -> Advanced -> Enable Global Shared Memory (SNMP) を確認し、管理者で再試行してください。";
        }

        return "Core Temp を起動し、Options -> Settings -> Advanced -> Enable Global Shared Memory (SNMP) を ON にして「再チェック」または「管理者でSensorHelperを再起動して再試行」を実行してください。";
    }

    private static async Task<bool> IsPortOpenAsync()
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            await client.ConnectAsync("127.0.0.1", 17805, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void RefreshBindings()
    {
        Dispatcher.Invoke(() =>
        {
            DataContext = null;
            DataContext = this;
        });
    }

    private Process? LaunchOrActivateCoreTemp()
    {
        var running = FindRunningCoreTempProcess();
        if (running is not null)
        {
            TryBringToFront(running);
            return running;
        }

        foreach (var exe in CoreTempExeCandidates())
        {
            if (!File.Exists(exe))
            {
                continue;
            }

            try
            {
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                });
                if (p is not null)
                {
                    return p;
                }
            }
            catch
            {
            }
        }

        try
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = "Core Temp.exe",
                UseShellExecute = true,
            });
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> CoreTempExeCandidates()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Core Temp", "Core Temp.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Core Temp", "Core Temp.exe");
    }

    private static Process? FindRunningCoreTempProcess()
    {
        foreach (var name in new[] { "Core Temp", "CoreTemp" })
        {
            var p = Process.GetProcessesByName(name).FirstOrDefault();
            if (p is not null)
            {
                return p;
            }
        }

        return null;
    }

    private static void TryBringToFront(Process process)
    {
        try
        {
            if (process.MainWindowHandle == IntPtr.Zero)
            {
                return;
            }

            ShowWindow(process.MainWindowHandle, 9);
            SetForegroundWindow(process.MainWindowHandle);
        }
        catch
        {
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (!CloseCoreTempOnExitConsent)
        {
            return;
        }

        try
        {
            foreach (var name in new[] { "Core Temp", "CoreTemp" })
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    p.Kill(entireProcessTree: true);
                }
            }
        }
        catch
        {
        }
    }
}
