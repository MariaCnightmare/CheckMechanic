using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection; // UPDATED
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CheckMechanic.Shared;
using Microsoft.Win32;

namespace CheckMechanic.Desktop;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const string HelperBaseUrl = "http://127.0.0.1:17805";
    private const string UpdateManifestUrl = "https://checkmechanic.apiron.jp/latest.json";
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(1.5) };
    private readonly HttpClient _updateHttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer _timer;

    // --- Commit A: polling concurrency guard ---
    private readonly SemaphoreSlim _pollGate = new(1, 1);

    private readonly Queue<double?> _tempHistory = new();
    private readonly Queue<double?> _cpuHistory = new();
    private readonly Queue<double?> _memHistory = new();
    private readonly Queue<double?> _gpuHistory = new();
    private readonly Queue<double?> _diskHistory = new();
    private readonly Queue<double?> _netHistory = new();
    private readonly List<int> _localScoreHistory = new();

    private string _lastTelemetryJson = "{}";
    private string _lastProfileJson = "{}";
    private int _pollCount;
    private bool _optInConsent;
    private bool _closeCoreTempOnExitConsent = true;
    private int _latestPerfScore;
    private string _latestPerfGrade = "N/A";
    private bool _isSwitchingToWidgetMode;
    private bool _coreTempArtifactsCleanupDone;
    private bool _isUpdateCheckRunning;
    private string? _availableUpdateVersion;
    private string? _availableUpdateDownloadUrl;
    private string? _availableUpdateReleaseNotesUrl;
    private string? _availableUpdateSha256;

    // --- Commit C: redraw suppression ---
    private int? _lastUiHash;

    // --- Commit B: INotifyPropertyChanged ---
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// まとめて更新通知（WPFは propertyName=null/empty を “全部変わった” 扱いします）
    /// </summary>
    private void NotifyAll()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public string CpuTemperatureText { get; set; } = "未取得";
    public string CpuKpiText { get; set; } = "--";
    public string CpuUtilizationText { get; set; } = "CPU使用率: 未取得";
    public string CpuAvgText { get; set; } = "avg(60s): --";
    public string CpuDetailText { get; set; } = "clock -- / power -- / source --";
    public string MemoryText { get; set; } = "未取得";
    public string MemoryKpiText { get; set; } = "--";
    public string MemorySubText { get; set; } = "--";
    public string GpuText { get; set; } = "N/A";
    public string GpuKpiText { get; set; } = "--";
    public string GpuSubText { get; set; } = "--";
    public string DiskText { get; set; } = "未取得";
    public string DiskCapacityText { get; set; } = "Free -- / Total --";
    public string DiskUsageText { get; set; } = "--";
    public string NetText { get; set; } = "未取得";
    public string NetMetaText { get; set; } = "adapter: -";
    public string BatteryText { get; set; } = "N/A";
    public string LastUpdateText { get; set; } = "--";
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
    public string DiagnosticTelemetryText { get; set; } = "(未取得)";
    public string ProviderErrorsText { get; set; } = "(none)";
    public string ProfileText { get; set; } = "(未取得)";
    public string RankingProfileText { get; set; } = "Opt-in をONにするとカテゴリを生成します。";
    public string TempChartData { get; set; } = string.Empty;
    public string CpuChartData { get; set; } = string.Empty;
    public string MemChartData { get; set; } = string.Empty;
    public string GpuChartData { get; set; } = string.Empty;
    public string DiskChartData { get; set; } = string.Empty;
    public string NetChartData { get; set; } = string.Empty;
    public ObservableCollection<ChartTick> TempChartTicks { get; } = new();
    public ObservableCollection<ChartTick> CpuChartTicks { get; } = new();
    public ObservableCollection<ChartTick> MemChartTicks { get; } = new();
    public ObservableCollection<ChartTick> GpuChartTicks { get; } = new();
    public ObservableCollection<ChartTick> DiskChartTicks { get; } = new();
    public ObservableCollection<ChartTick> NetChartTicks { get; } = new();
    public double TempLatestPointX { get; set; }
    public double TempLatestPointY { get; set; }
    public double CpuLatestPointX { get; set; }
    public double CpuLatestPointY { get; set; }
    public double MemLatestPointX { get; set; }
    public double MemLatestPointY { get; set; }
    public double GpuLatestPointX { get; set; }
    public double GpuLatestPointY { get; set; }
    public double DiskLatestPointX { get; set; }
    public double DiskLatestPointY { get; set; }
    public double NetLatestPointX { get; set; }
    public double NetLatestPointY { get; set; }
    public Visibility TempLatestPointVisibility { get; set; } = Visibility.Collapsed;
    public Visibility CpuLatestPointVisibility { get; set; } = Visibility.Collapsed;
    public Visibility MemLatestPointVisibility { get; set; } = Visibility.Collapsed;
    public Visibility GpuLatestPointVisibility { get; set; } = Visibility.Collapsed;
    public Visibility DiskLatestPointVisibility { get; set; } = Visibility.Collapsed;
    public Visibility NetLatestPointVisibility { get; set; } = Visibility.Collapsed;
    public string TempRingArcData { get; set; } = string.Empty;
    public string TempRingCenterText { get; set; } = "--";
    public string TempLatestText { get; set; } = "--";
    public string TempMin60Text { get; set; } = "--";
    public string TempMax60Text { get; set; } = "--";
    public string TempSourceText { get; set; } = "source: unavailable";
    public string TempSourceTooltipText { get; set; } = "source: unavailable";
    public string NetDetailTooltipText { get; set; } = string.Empty;
    public string TempStatsText { get; set; } = "Latest -- / Min -- / Max --";
    public string CpuLatestText { get; set; } = "--";
    public string CpuAvg60Text { get; set; } = "--";
    public string MemLatestText { get; set; } = "--";
    public string GpuLatestText { get; set; } = "--";
    public string DiskLatestText { get; set; } = "--";
    public string NetLatestText { get; set; } = "--";
    public string MemoryAvailableText { get; set; } = "--";
    public double CpuUtilizationValue { get; set; }
    public double MemoryUtilizationValue { get; set; }
    public double GpuUtilizationValue { get; set; }
    public double ScoreCpuValue { get; set; }
    public double ScoreMemValue { get; set; }
    public double ScoreDiskValue { get; set; }
    public double ScoreNetValue { get; set; }
    public double ScoreTempValue { get; set; }
    public double ScoreGpuValue { get; set; }
    public string ScoreCpuText { get; set; } = "--";
    public string ScoreMemText { get; set; } = "--";
    public string ScoreDiskText { get; set; } = "--";
    public string ScoreNetText { get; set; } = "--";
    public string ScoreTempText { get; set; } = "--";
    public string ScoreGpuText { get; set; } = "--";
    public string ScoreCpuReason { get; set; } = "-";
    public string ScoreMemReason { get; set; } = "-";
    public string ScoreDiskReason { get; set; } = "-";
    public string ScoreNetReason { get; set; } = "-";
    public string ScoreTempReason { get; set; } = "-";
    public string ScoreGpuReason { get; set; } = "-";
    public string RadarGrid100Points { get; set; } = string.Empty;
    public string RadarGrid80Points { get; set; } = string.Empty;
    public string RadarGrid60Points { get; set; } = string.Empty;
    public string RadarGrid40Points { get; set; } = string.Empty;
    public string RadarGrid20Points { get; set; } = string.Empty;
    public string RadarScorePoints { get; set; } = string.Empty;
    public string RadarCpuLabel { get; set; } = "CPU --";
    public string RadarMemLabel { get; set; } = "Memory --";
    public string RadarDiskLabel { get; set; } = "Disk --";
    public string RadarNetLabel { get; set; } = "Network --";
    public string RadarThermalLabel { get; set; } = "Thermal --";
    public string RadarGpuLabel { get; set; } = "GPU --";
    public ObservableCollection<string> ScoreInsights { get; } = new();
    public Visibility RestrictionVisibility { get; set; } = Visibility.Visible;
    public string DiagnosticsSummaryText { get; set; } = "errors:0 / provider:-";
    public string LocalRankingText { get; set; } = "ローカル履歴: N/A";
    public Visibility UpdateBadgeVisibility { get; set; } = Visibility.Visible;
    public string UpdateBadgeText { get; set; } = "更新を確認";
    public string UpdateBadgeTooltip { get; set; } = string.Empty;
    public string AppVersionText { get; set; } = "Version --";

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
    public string ProfileStorageModel { get; set; } = "-";
    public string ProfileStorageBusType { get; set; } = "-";
    public string ProfileDeviceClass { get; set; } = "-";
    public string ProfileTempProvider { get; set; } = "-";
    public string ProfileMachineVendor { get; set; } = "-";
    public string ProfileMachineModel { get; set; } = "-";
    public string ProfileGpuDriverVersion { get; set; } = "-";
    public string ProfileUptimeText { get; set; } = "-";
    public ObservableCollection<string> Logs { get; } = new();

    public bool OptInConsent
    {
        get => _optInConsent;
        set
        {
            if (_optInConsent == value)
            {
                return;
            }

            _optInConsent = value;
            OnPropertyChanged(nameof(OptInConsent));
            UpdateRankingPreview();
            UpdateLocalRankingText();
            RefreshBindings(force: true);
        }
    }

    public bool CloseCoreTempOnExitConsent
    {
        get => _closeCoreTempOnExitConsent;
        set
        {
            if (_closeCoreTempOnExitConsent == value)
            {
                return;
            }

            _closeCoreTempOnExitConsent = value;
            OnPropertyChanged(nameof(CloseCoreTempOnExitConsent));
            SaveUiSettings();
            RefreshBindings(force: true);
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        AppVersionText = $"Version {GetCurrentAppVersionString()}";
        InitializeRadarGrid();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };

        // --- Commit A: avoid overlapping polling calls ---
        _timer.Tick += OnTimerTick;

        Loaded += async (_, _) => await InitializeAsync();
        Closing += MainWindow_OnClosing;
    }

    // --- Commit A: guarded tick handler (no overlap) ---
    private async void OnTimerTick(object? sender, EventArgs e)
    {
        if (!_pollGate.Wait(0))
        {
            return;
        }

        try
        {
            await PollAllAsync();
        }
        catch
        {
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private async Task InitializeAsync()
    {
        LoadUiSettings();
        LoadLocalScoreHistory();
        CleanupCoreTempInstallerArtifacts();
        _ = RunUpdateCheckAsync(force: false);
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
                CpuDetailText = "clock -- / power -- / source --";
                DiskCapacityText = "Free -- / Total --";
                DiskUsageText = "--";
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
                CpuDetailText = "clock -- / power -- / source --";
                DiskCapacityText = "Free -- / Total --";
                DiskUsageText = "--";
                SetRestrictedMode(true, "必須要件未達: 温度取得が必要です。", "Core Temp を起動した状態で再チェックしてください。");
                AddLog("telemetry parse failed");
                RefreshBindings();
                return;
            }

            CpuUtilizationText = payload.Cpu.UtilPercent is double utilVal ? $"CPU使用率: {utilVal:F1}%" : "CPU使用率: 未取得";
            CpuUtilizationValue = payload.Cpu.UtilPercent is double cpuVal ? Math.Clamp(cpuVal, 0, 100) : 0;
            CpuKpiText = payload.Cpu.UtilPercent is double cpuNow ? $"{cpuNow:F1}%" : "--";
            CpuDetailText = BuildCpuDetailText(payload.Cpu);

            MemoryText = BuildMemoryText(payload.Memory);
            MemoryUtilizationValue = payload.Memory.UtilPercent is double memVal ? Math.Clamp(memVal, 0, 100) : 0;
            MemoryAvailableText = BuildMemoryAvailableText(payload.Memory);
            MemoryKpiText = BuildMemoryKpiText(payload.Memory);
            MemorySubText = payload.Memory.UtilPercent is double memNow ? $"({memNow:F1}%) / available {MemoryAvailableText}" : $"available {MemoryAvailableText}";

            GpuKpiText = payload.Gpu.UtilPercent is double gpuNow ? $"{gpuNow:F1}%" : "N/A";
            GpuUtilizationValue = payload.Gpu.UtilPercent is double gpuVal ? Math.Clamp(gpuVal, 0, 100) : 0;
            GpuText = payload.Gpu.Name ?? "N/A";
            GpuSubText = BuildGpuSubText(payload.Gpu);

            DiskText = $"R {FormatRate(payload.Disk.ReadBps)} / W {FormatRate(payload.Disk.WriteBps)}";
            DiskCapacityText = BuildDiskCapacityText(payload.Disk.TotalGb, payload.Disk.FreeGb);
            DiskUsageText = BuildDiskUsageText(payload.Disk.TotalGb, payload.Disk.FreeGb);

            NetText = $"↓ {FormatRate(payload.Net.RecvBps)}  ↑ {FormatRate(payload.Net.SentBps)}";
            if (!string.IsNullOrWhiteSpace(payload.Net.ActiveAdapterName))
            {
                var link = payload.Net.LinkSpeedMbps.HasValue
                    ? payload.Net.LinkSpeedMbps >= 1000
                        ? $"{payload.Net.LinkSpeedMbps / 1000d:F1} Gbps"
                        : $"{payload.Net.LinkSpeedMbps:F0} Mbps"
                    : "n/a";
                NetMetaText = $"{ShortAdapterName(payload.Net.ActiveAdapterName)} • {link}";
                NetDetailTooltipText = $"{payload.Net.ActiveAdapterName} • {link}";
            }
            else
            {
                NetMetaText = "adapter: unavailable";
                NetDetailTooltipText = string.Empty;
            }

            BatteryText = BuildBatteryText(payload.Battery);

            CpuLatestText = payload.Cpu.UtilPercent is double cu ? $"{cu:F1}%" : "--";
            MemLatestText = payload.Memory.UtilPercent is double mu ? $"{mu:F1}%" : "--";
            GpuLatestText = payload.Gpu.UtilPercent is double gu ? $"{gu:F1}%" : "N/A";
            DiskLatestText = FormatRate((payload.Disk.ReadBps ?? 0) + (payload.Disk.WriteBps ?? 0));
            NetLatestText = FormatRate((payload.Net.RecvBps ?? 0) + (payload.Net.SentBps ?? 0));

            TempSourceText = $"source: {payload.Cpu.ProviderUsed ?? payload.Cpu.Source ?? "unavailable"}";
            TempSourceTooltipText = TempSourceText;

            if (payload.Cpu.TempC is double temp)
            {
                CpuTemperatureText = $"{temp:F1} °C";
                TempLatestText = $"{temp:F1}°C";
                TempRingCenterText = $"{temp:F0}";
                TempRingArcData = BuildRingArcPath(temp, 30, 100, 74, 8);
                SetStatus("✅ OK");
                SetRestrictedMode(false, string.Empty, string.Empty);
                AddLog($"temp={temp:F1}C util={payload.Cpu.UtilPercent?.ToString("F1") ?? "n/a"} provider={payload.Cpu.ProviderUsed ?? "unknown"} label={payload.Cpu.Label} clock={payload.Cpu.ClockMhz?.ToString("F0") ?? "n/a"}MHz");
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
                payload.Gpu.UtilPercent,
                diskTotal,
                netTotal);

            UpdateScore(payload);
            RefreshChartData();

            TempStatsText = $"Latest {TempLatestText} / Min {TempMin60Text} / Max {TempMax60Text}";
            CpuAvgText = $"avg(60s): {CpuAvg60Text}";

            DiagnosticsSummaryText = BuildDiagnosticsSummary(payload);
            ProviderErrorsText = payload.Cpu.ProviderErrors?.Count > 0
                ? string.Join(Environment.NewLine, payload.Cpu.ProviderErrors)
                : "(none)";

            DiagnosticTelemetryText = PrettyJson(_lastTelemetryJson);

            RefreshBindings();
        }
        catch (HttpRequestException ex)
        {
            CpuTemperatureText = "未取得";
            CpuDetailText = "clock -- / power -- / source --";
            DiskCapacityText = "Free -- / Total --";
            DiskUsageText = "--";
            TempLatestText = "--";
            TempRingCenterText = "R";
            TempRingArcData = string.Empty;
            TempSourceText = "source: unavailable";
            SetStatus("❌ SensorHelper 未接続");
            SetRestrictedMode(true, "必須要件未達: 温度取得が必要です。", "Core Temp を起動した状態で再チェックしてください。");
            AddLog(SanitizeError(ex.Message));
            ProviderErrorsText = "(helper_unavailable)";
            RefreshBindings();
        }
        catch
        {
            CpuTemperatureText = "未取得";
            CpuDetailText = "clock -- / power -- / source --";
            DiskCapacityText = "Free -- / Total --";
            DiskUsageText = "--";
            TempLatestText = "--";
            TempRingCenterText = "R";
            TempRingArcData = string.Empty;
            TempSourceText = "source: unavailable";
            SetStatus("❌ 取得失敗");
            SetRestrictedMode(true, "必須要件未達: 温度取得が必要です。", "Core Temp を起動した状態で再チェックしてください。");
            AddLog("telemetry request failed");
            ProviderErrorsText = "(telemetry_request_failed)";
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
                RefreshBindings();
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
            ProfileStorageModel = profile.StorageModel ?? "-";
            ProfileStorageBusType = profile.StorageBusType ?? "-";
            ProfileDeviceClass = profile.DeviceClass ?? "-";
            ProfileTempProvider = profile.TempProvider ?? "-";
            ProfileMachineVendor = profile.MachineVendor ?? "-";
            ProfileMachineModel = profile.MachineModel ?? "-";
            ProfileGpuDriverVersion = profile.GpuDriverVersion ?? "-";
            ProfileUptimeText = profile.UptimeHours.HasValue ? $"{profile.UptimeHours.Value:F1} h" : "-";

            UpdateRankingPreview();
            RefreshBindings();
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
            ProfileStorageModel = "-";
            ProfileStorageBusType = "-";
            ProfileDeviceClass = "-";
            ProfileTempProvider = "-";
            ProfileMachineVendor = "-";
            ProfileMachineModel = "-";
            ProfileGpuDriverVersion = "-";
            ProfileUptimeText = "-";

            UpdateRankingPreview();
            RefreshBindings();
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
        var gpuHistory = _gpuHistory.Where(x => x.HasValue).Select(x => x!.Value).ToList();

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
            ScoreGpuValue = 0;
            ScoreCpuText = "--";
            ScoreMemText = "--";
            ScoreDiskText = "--";
            ScoreNetText = "--";
            ScoreTempText = "--";
            ScoreGpuText = "--";
            ScoreCpuReason = "温度要件未達";
            ScoreMemReason = "温度要件未達";
            ScoreDiskReason = "温度要件未達";
            ScoreNetReason = "温度要件未達";
            ScoreTempReason = "温度要件未達";
            ScoreGpuReason = "温度要件未達";
            UpdateRadar(new[] { 0d, 0d, 0d, 0d, 0d, 0d });
            ScoreInsights.Clear();
            ScoreInsights.Add("温度未取得のためスコア評価を停止しています。");
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

        var gpuHeadroom = gpuHistory.Count > 0 ? Math.Clamp(100 - gpuHistory.Average(), 0, 100) : (double?)null;
        ScoreGpuValue = Math.Round(gpuHeadroom ?? 0, 1);

        ScoreCpuText = $"{ScoreCpuValue:F0}";
        ScoreMemText = $"{ScoreMemValue:F0}";
        ScoreDiskText = result.DiskHeadroom.HasValue ? $"{ScoreDiskValue:F0}" : "N/A";
        ScoreNetText = result.NetworkHeadroom.HasValue ? $"{ScoreNetValue:F0}" : "N/A";
        ScoreTempText = $"{ScoreTempValue:F0}";
        ScoreGpuText = gpuHeadroom.HasValue ? $"{ScoreGpuValue:F0}" : "N/A";

        ScoreCpuReason = ScoreCpuValue < 40 ? "CPU負荷が高い" : "CPU余力あり";
        ScoreMemReason = ScoreMemValue < 40 ? "メモリ使用率が高い" : "メモリ余力あり";
        ScoreDiskReason = result.DiskHeadroom.HasValue
            ? (ScoreDiskValue < 40 ? "I/O負荷が高い" : "I/O余力あり")
            : "ディスク指標なし";
        ScoreNetReason = result.NetworkHeadroom.HasValue
            ? (ScoreNetValue < 40 ? "ネットワーク負荷が高い" : "ネットワーク余力あり")
            : "ネットワーク指標なし";
        ScoreTempReason = result.TempPenalty > 0 ? $"温度ペナルティ {result.TempPenalty:F0}" : "温度ペナルティなし";
        ScoreGpuReason = gpuHeadroom.HasValue
            ? (ScoreGpuValue < 40 ? "GPU負荷が高い" : "GPU余力あり")
            : "GPU指標なし";

        UpdateRadar(new[] { ScoreCpuValue, ScoreMemValue, ScoreDiskValue, ScoreNetValue, ScoreTempValue, ScoreGpuValue });
        UpdateScoreInsights();

        PersistLocalScoreIfOptIn(result.Score);
        UpdateLocalRankingText();
    }

    private void AppendHistory(double? temp, double? cpu, double? mem, double? gpu, double? diskTotalBps, double? netTotalBps)
    {
        AppendWithLimit(_tempHistory, temp, 60);
        AppendWithLimit(_cpuHistory, cpu, 60);
        AppendWithLimit(_memHistory, mem, 60);
        AppendWithLimit(_gpuHistory, gpu, 60);
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
        const double plotWidth = 288;
        const double plotHeight = 180;
        const int ticks = 4;

        var tempValues = _tempHistory.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        var tempMinRaw = tempValues.Count > 0 ? tempValues.Min() : 30;
        var tempMaxRaw = tempValues.Count > 0 ? tempValues.Max() : 100;
        var tempMin = Math.Floor((tempMinRaw - 2) * 10) / 10.0;
        var tempMax = Math.Ceiling((tempMaxRaw + 2) * 10) / 10.0;
        if (tempMax - tempMin < 8)
        {
            tempMax = tempMin + 8;
        }

        TempChartData = BuildSparklinePath(_tempHistory, plotWidth, plotHeight, tempMin, tempMax);
        UpdateTicks(TempChartTicks, ticks, tempMin, tempMax, plotHeight, "°C", 1);
        UpdateLatestPoint(_tempHistory, plotWidth, plotHeight, tempMin, tempMax, out var tx, out var ty, out var tv);
        TempLatestPointX = tx;
        TempLatestPointY = ty;
        TempLatestPointVisibility = tv;

        TempMin60Text = tempValues.Count > 0 ? $"{tempValues.Min():F1}°C" : "--";
        TempMax60Text = tempValues.Count > 0 ? $"{tempValues.Max():F1}°C" : "--";

        TempLatestText = _tempHistory.LastOrDefault(x => x.HasValue) is double lastTemp ? $"{lastTemp:F1}°C" : "N/A";

        CpuChartData = BuildSparklinePath(_cpuHistory, plotWidth, plotHeight, 0, 100);
        UpdateTicks(CpuChartTicks, ticks, 0, 100, plotHeight, "%", 0);
        UpdateLatestPoint(_cpuHistory, plotWidth, plotHeight, 0, 100, out var cx, out var cy, out var cv);
        CpuLatestPointX = cx;
        CpuLatestPointY = cy;
        CpuLatestPointVisibility = cv;

        MemChartData = BuildSparklinePath(_memHistory, plotWidth, plotHeight, 0, 100);
        UpdateTicks(MemChartTicks, ticks, 0, 100, plotHeight, "%", 0);
        UpdateLatestPoint(_memHistory, plotWidth, plotHeight, 0, 100, out var mx, out var my, out var mv);
        MemLatestPointX = mx;
        MemLatestPointY = my;
        MemLatestPointVisibility = mv;

        GpuChartData = BuildSparklinePath(_gpuHistory, plotWidth, plotHeight, 0, 100);
        UpdateTicks(GpuChartTicks, ticks, 0, 100, plotHeight, "%", 0);
        UpdateLatestPoint(_gpuHistory, plotWidth, plotHeight, 0, 100, out var gx, out var gy, out var gv);
        GpuLatestPointX = gx;
        GpuLatestPointY = gy;
        GpuLatestPointVisibility = gv;

        var diskValues = _diskHistory.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        var diskMax = Math.Max(1d, diskValues.Count > 0 ? diskValues.Max() : 1d);
        DiskChartData = BuildSparklinePath(_diskHistory, plotWidth, plotHeight, 0, diskMax);
        UpdateTicks(DiskChartTicks, ticks, 0, diskMax, plotHeight, "", 0);
        UpdateLatestPoint(_diskHistory, plotWidth, plotHeight, 0, diskMax, out var dx, out var dy, out var dv);
        DiskLatestPointX = dx;
        DiskLatestPointY = dy;
        DiskLatestPointVisibility = dv;

        var netValues = _netHistory.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        var netMax = Math.Max(1d, netValues.Count > 0 ? netValues.Max() : 1d);
        NetChartData = BuildSparklinePath(_netHistory, plotWidth, plotHeight, 0, netMax);
        UpdateTicks(NetChartTicks, ticks, 0, netMax, plotHeight, "", 0);
        UpdateLatestPoint(_netHistory, plotWidth, plotHeight, 0, netMax, out var nx, out var ny, out var nv);
        NetLatestPointX = nx;
        NetLatestPointY = ny;
        NetLatestPointVisibility = nv;

        var cpuValues = _cpuHistory.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        CpuAvg60Text = cpuValues.Count > 0 ? $"{cpuValues.Average():F1}%" : "--";
        CpuLatestText = _cpuHistory.LastOrDefault(x => x.HasValue) is double lastCpu ? $"{lastCpu:F1}%" : "N/A";
        MemLatestText = _memHistory.LastOrDefault(x => x.HasValue) is double lastMem ? $"{lastMem:F1}%" : "N/A";
        GpuLatestText = _gpuHistory.LastOrDefault(x => x.HasValue) is double lastGpu ? $"{lastGpu:F1}%" : "N/A";
        DiskLatestText = _diskHistory.LastOrDefault(x => x.HasValue) is double lastDisk ? FormatRate(lastDisk) : "N/A";
        NetLatestText = _netHistory.LastOrDefault(x => x.HasValue) is double lastNet ? FormatRate(lastNet) : "N/A";
    }

    private static void UpdateTicks(ObservableCollection<ChartTick> target, int ticks, double min, double max, double plotHeight, string unit, int decimals)
    {
        target.Clear();
        const double labelHeight = 16;
        const double axisPadding = 4;
        var maxTop = Math.Max(axisPadding, plotHeight - labelHeight - axisPadding);
        for (var i = 0; i < ticks; i++)
        {
            var ratio = (double)i / (ticks - 1);
            var value = max - ((max - min) * ratio);
            var y = ratio * plotHeight;
            string label;
            if (string.IsNullOrWhiteSpace(unit))
            {
                label = FormatRate(value);
            }
            else
            {
                label = decimals > 0 ? $"{value:F1}{unit}" : $"{value:F0}{unit}";
            }
            var top = Math.Clamp(y - (labelHeight / 2.0), axisPadding, maxTop);
            target.Add(new ChartTick { Y = top, Label = label });
        }
    }

    private static void UpdateLatestPoint(
        IEnumerable<double?> values,
        double width,
        double height,
        double min,
        double max,
        out double x,
        out double y,
        out Visibility visibility)
    {
        var list = values.ToList();
        var index = -1;
        double? value = null;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].HasValue)
            {
                index = i;
                value = list[i];
                break;
            }
        }

        if (index < 0 || !value.HasValue || list.Count < 2 || max <= min)
        {
            x = 0;
            y = 0;
            visibility = Visibility.Collapsed;
            return;
        }

        var stepX = width / Math.Max(list.Count - 1, 1);
        x = Math.Max(0, Math.Min(width - 7, index * stepX - 3.5));
        var normalized = Math.Clamp((value.Value - min) / (max - min), 0, 1);
        y = Math.Max(0, Math.Min(height - 7, (height - (normalized * height)) - 3.5));
        visibility = Visibility.Visible;
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

    private static string BuildGpuSubText(GpuTelemetry gpu)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(gpu.Vendor))
        {
            parts.Add(gpu.Vendor);
        }
        if (gpu.VramUsedMb.HasValue || gpu.VramTotalMb.HasValue)
        {
            var used = gpu.VramUsedMb.HasValue ? $"{gpu.VramUsedMb.Value / 1024d:F1}GB" : "?";
            var total = gpu.VramTotalMb.HasValue ? $"{gpu.VramTotalMb.Value / 1024d:F1}GB" : "?";
            if (gpu.VramUsedMb.HasValue && gpu.VramTotalMb.HasValue && gpu.VramUsedMb.Value > gpu.VramTotalMb.Value)
            {
                parts.Add($"VRAM {used} (total est {total})");
            }
            else
            {
                parts.Add($"VRAM {used}/{total}");
            }
        }
        if (gpu.TemperatureC.HasValue)
        {
            parts.Add($"{gpu.TemperatureC.Value:F1}°C");
        }
        if (!string.IsNullOrWhiteSpace(gpu.DriverVersion))
        {
            parts.Add($"drv {gpu.DriverVersion}");
        }
        if (gpu.CoreClockMhz.HasValue)
        {
            parts.Add($"core {gpu.CoreClockMhz.Value:F0}MHz");
        }
        if (gpu.MemoryClockMhz.HasValue)
        {
            parts.Add($"mem {gpu.MemoryClockMhz.Value:F}MHz");
        }
        return parts.Count == 0 ? "N/A" : string.Join(" / ", parts);
    }

    private static string BuildCpuDetailText(CpuTelemetry cpu)
    {
        var clock = cpu.ClockMhz.HasValue ? $"{cpu.ClockMhz.Value:F0}MHz" : "n/a";
        var power = cpu.PowerW.HasValue ? $"{cpu.PowerW.Value:F1}W" : "n/a";
        var source = cpu.ProviderUsed ?? cpu.Source ?? "unknown";
        var label = string.IsNullOrWhiteSpace(cpu.Label) ? "-" : cpu.Label;
        return $"clock {clock} / power {power} / source {source} / label {label}";
    }

    private static string BuildBatteryText(object? battery)
    {
        if (battery is null)
        {
            return "N/A";
        }

        var type = battery.GetType();
        var percentObj = type.GetProperty("Percent")?.GetValue(battery);
        var chargingObj = type.GetProperty("IsCharging")?.GetValue(battery);
        var dischargeObj = type.GetProperty("DischargeW")?.GetValue(battery);

        var percent = percentObj as double? ?? (percentObj is double p ? p : null);
        var isCharging = chargingObj as bool? ?? (chargingObj is bool c ? c : null);
        var dischargeW = dischargeObj as double? ?? (dischargeObj is double d ? d : null);

        if (!percent.HasValue && !isCharging.HasValue)
        {
            return "N/A";
        }

        var parts = new List<string>();
        if (percent.HasValue)
        {
            parts.Add($"{percent.Value:F0}%");
        }
        if (isCharging.HasValue)
        {
            parts.Add(isCharging.Value ? "charging" : "discharging");
        }
        if (dischargeW.HasValue)
        {
            parts.Add($"{dischargeW.Value:F1}W");
        }
        return string.Join(" / ", parts);
    }

    // --- Commit C: time-dependent string removed to allow stable hashing ---
    private static string BuildDiagnosticsSummary(TelemetryResponse payload)
    {
        var provider = payload.Cpu.ProviderUsed ?? "none";
        var errors = payload.Cpu.ProviderErrors?.Count ?? 0;
        return $"errors:{errors} / provider:{provider}";
    }

    private void InitializeRadarGrid()
    {
        RadarGrid100Points = BuildRadarPoints(new[] { 100d, 100d, 100d, 100d, 100d, 100d });
        RadarGrid80Points = BuildRadarPoints(new[] { 80d, 80d, 80d, 80d, 80d, 80d });
        RadarGrid60Points = BuildRadarPoints(new[] { 60d, 60d, 60d, 60d, 60d, 60d });
        RadarGrid40Points = BuildRadarPoints(new[] { 40d, 40d, 40d, 40d, 40d, 40d });
        RadarGrid20Points = BuildRadarPoints(new[] { 20d, 20d, 20d, 20d, 20d, 20d });
        RadarScorePoints = BuildRadarPoints(new[] { 0d, 0d, 0d, 0d, 0d, 0d });
    }

    private void UpdateRadar(IReadOnlyList<double> values)
    {
        RadarScorePoints = BuildRadarPoints(values);
        RadarCpuLabel = $"CPU {values[0]:F0}";
        RadarMemLabel = $"Memory {values[1]:F0}";
        RadarDiskLabel = $"Disk {values[2]:F0}";
        RadarNetLabel = $"Network {values[3]:F0}";
        RadarThermalLabel = $"Thermal {values[4]:F0}";
        RadarGpuLabel = $"GPU {values[5]:F0}";
    }

    private static string BuildRadarPoints(IReadOnlyList<double> scores)
    {
        const double cx = 140;
        const double cy = 115;
        const double r = 78;
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 6; i++)
        {
            var ratio = Math.Clamp(scores[i] / 100.0, 0, 1);
            var angle = ((Math.PI * 2) / 6 * i) - (Math.PI / 2);
            var x = cx + (Math.Cos(angle) * r * ratio);
            var y = cy + (Math.Sin(angle) * r * ratio);
            if (i > 0)
            {
                sb.Append(' ');
            }
            sb.Append($"{x:F1},{y:F1}");
        }
        return sb.ToString();
    }

    private void UpdateScoreInsights()
    {
        var candidates = new List<(double score, string text)>
        {
            (ScoreCpuValue, ScoreCpuReason),
            (ScoreMemValue, ScoreMemReason),
            (ScoreDiskValue, ScoreDiskReason),
            (ScoreNetValue, ScoreNetReason),
            (ScoreTempValue, ScoreTempReason),
            (ScoreGpuValue, ScoreGpuReason),
        };

        var alerts = candidates
            .Where(x => x.score < 40)
            .OrderBy(x => x.score)
            .Select(x => x.text)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .Take(3)
            .ToList();

        ScoreInsights.Clear();
        if (alerts.Count == 0)
        {
            ScoreInsights.Add("重大なボトルネックは検出されていません");
            return;
        }

        foreach (var alert in alerts)
        {
            ScoreInsights.Add(alert);
        }
    }

    private static string ShortAdapterName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        return name.Replace(" (Hyper-V firewall)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("vEthernet (WSL", "vEthernet (WSL", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    public sealed class ChartTick
    {
        public double Y { get; set; }
        public string Label { get; set; } = string.Empty;
    }

    private static string GetHistoryFilePath()
    {
        var dir = UiSettingsStore.GetAppDataDirectory();
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "perf_history.json");
    }

    private void LoadUiSettings()
    {
        var settings = UiSettingsStore.Load();
        _closeCoreTempOnExitConsent = settings.CloseCoreTempOnExit;
    }

    private void SaveUiSettings()
    {
        try
        {
            var settings = UiSettingsStore.Load();
            settings.CloseCoreTempOnExit = _closeCoreTempOnExitConsent;
            UiSettingsStore.Save(settings);
        }
        catch
        {
            AddLog("ui settings write failed");
        }
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

    private static string BuildDiskCapacityText(double? totalGb, double? freeGb)
    {
        if (!totalGb.HasValue && !freeGb.HasValue)
        {
            return "Free -- / Total --";
        }

        var free = freeGb.HasValue ? $"{freeGb.Value:F0}" : "?";
        var total = totalGb.HasValue ? $"{totalGb.Value:F0}" : "?";
        return $"Free {free} GB / Total {total} GB";
    }

    private static string BuildDiskUsageText(double? totalGb, double? freeGb)
    {
        if (!totalGb.HasValue || !freeGb.HasValue || totalGb.Value <= 0)
        {
            return "--";
        }

        var usedPercent = Math.Clamp(((totalGb.Value - freeGb.Value) / totalGb.Value) * 100d, 0, 100);
        return $"{usedPercent:F1}%";
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
        RefreshBindings(force: true);
    }

    private async void RecheckButton_OnClick(object sender, RoutedEventArgs e)
    {
        await EnsureHelperAvailableAsync();
        await PollAllAsync(forceProfile: true);
        await RefreshDiagnosticsAsync();
        RefreshBindings(force: true);
    }

    private async void UpdateBadgeButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunUpdateCheckAsync(force: true);
        if (string.IsNullOrWhiteSpace(_availableUpdateVersion) ||
            string.IsNullOrWhiteSpace(_availableUpdateDownloadUrl) ||
            string.IsNullOrWhiteSpace(_availableUpdateSha256))
        {
            MessageBox.Show(this, "現在利用可能な更新は見つかりませんでした。", "CheckMechanic Update", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var notes = string.IsNullOrWhiteSpace(_availableUpdateReleaseNotesUrl) ? "-" : _availableUpdateReleaseNotesUrl;
        var confirm = MessageBox.Show(
            this,
            $"新しいバージョン {_availableUpdateVersion} が利用可能です。{Environment.NewLine}{Environment.NewLine}リリースノート: {notes}{Environment.NewLine}{Environment.NewLine}更新を開始しますか？",
            "CheckMechanic Update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        await DownloadAndRunUpdaterAsync();
    }

    private async Task RunUpdateCheckAsync(bool force)
    {
        if (_isUpdateCheckRunning)
        {
            return;
        }

        _isUpdateCheckRunning = true;
        try
        {
            if (!force && !await ShouldRunUpdateCheckAsync())
            {
                return;
            }

            var manifest = await FetchLatestManifestAsync();
            await SaveUpdateCheckStateAsync(DateTimeOffset.UtcNow);
            if (manifest is null)
            {
                return;
            }

            var currentVersion = GetCurrentAppVersionString();
            AddLog($"update compare: currentVersion={currentVersion} latestVersion={manifest.Version}"); // UPDATED
            if (IsNewerVersion(manifest.Version, currentVersion))
            {
                _availableUpdateVersion = manifest.Version;
                _availableUpdateDownloadUrl = manifest.DownloadUrl;
                _availableUpdateReleaseNotesUrl = manifest.ReleaseNotesUrl;
                _availableUpdateSha256 = manifest.Sha256;
                UpdateBadgeText = $"更新 v{manifest.Version}";
                UpdateBadgeTooltip = $"新しいバージョン: {manifest.Version}";
                UpdateBadgeVisibility = Visibility.Visible;
                AddLog($"update decision: update available (latest={manifest.Version}, current={currentVersion})"); // UPDATED
            }
            else
            {
                _availableUpdateVersion = null;
                _availableUpdateDownloadUrl = null;
                _availableUpdateReleaseNotesUrl = null;
                _availableUpdateSha256 = null;
                UpdateBadgeText = "更新を確認";
                UpdateBadgeTooltip = string.Empty;
                UpdateBadgeVisibility = Visibility.Visible;
                AddLog($"update decision: up to date (latest={manifest.Version}, current={currentVersion})"); // UPDATED
            }

            RefreshBindings(force: true);
        }
        catch (Exception ex)
        {
            AddLog($"update check failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _isUpdateCheckRunning = false;
        }
    }

    private async Task<bool> ShouldRunUpdateCheckAsync()
    {
        var state = await LoadUpdateCheckStateAsync();
        if (state?.LastCheckedUtc is not DateTimeOffset last)
        {
            return true;
        }

        return DateTimeOffset.UtcNow - last >= UpdateCheckInterval;
    }

    private async Task<UpdateManifest?> FetchLatestManifestAsync()
    {
        try
        {
            using var response = await _updateHttpClient.GetAsync(UpdateManifestUrl);
            if (!response.IsSuccessStatusCode)
            {
                AddLog($"update check HTTP {(int)response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions);
            if (manifest is null ||
                string.IsNullOrWhiteSpace(manifest.Version) ||
                string.IsNullOrWhiteSpace(manifest.DownloadUrl) ||
                string.IsNullOrWhiteSpace(manifest.Sha256))
            {
                AddLog("update manifest invalid");
                return null;
            }

            return manifest;
        }
        catch (Exception ex)
        {
            AddLog($"update check request failed: {ex.GetType().Name}");
            return null;
        }
    }

    private async Task DownloadAndRunUpdaterAsync()
    {
        if (string.IsNullOrWhiteSpace(_availableUpdateVersion) ||
            string.IsNullOrWhiteSpace(_availableUpdateDownloadUrl) ||
            string.IsNullOrWhiteSpace(_availableUpdateSha256))
        {
            return;
        }

        try
        {
            var updateDir = GetUpdateVersionDirectory(_availableUpdateVersion);
            Directory.CreateDirectory(updateDir);

            var setupPath = Path.Combine(updateDir, "Setup.exe");
            using (var response = await _updateHttpClient.GetAsync(_availableUpdateDownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var target = new FileStream(setupPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(target);
            }

            var actualSha256 = await ComputeSha256Async(setupPath);
            var expectedSha256 = NormalizeSha256(_availableUpdateSha256);
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                AddLog("update package verification failed");
                MessageBox.Show(this, "更新ファイルの検証に失敗しました。更新を中止します。", "CheckMechanic Update", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var burnLogPath = Path.Combine(updateDir, $"burn_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            var startInfo = new ProcessStartInfo
            {
                FileName = setupPath,
                Arguments = $"/passive /norestart /log \"{burnLogPath}\"",
                UseShellExecute = true,
                WorkingDirectory = updateDir,
            };

            var process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException("Setup.exe launch failed");
            }

            AddLog($"update installer started: {setupPath}");
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            AddLog($"update run failed: {ex.GetType().Name}: {ex.Message}");
            MessageBox.Show(this, "更新の実行に失敗しました。Diagnostics/ログを確認してください。", "CheckMechanic Update", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task<UpdateCheckState?> LoadUpdateCheckStateAsync()
    {
        try
        {
            var path = GetUpdateCheckStatePath();
            if (!File.Exists(path))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<UpdateCheckState>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private async Task SaveUpdateCheckStateAsync(DateTimeOffset checkedUtc)
    {
        try
        {
            var path = GetUpdateCheckStatePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var state = new UpdateCheckState { LastCheckedUtc = checkedUtc };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch
        {
        }
    }

    private static string GetUpdateCheckStatePath()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CheckMechanic", "updates");
        return Path.Combine(root, "update_state.json");
    }

    private static string GetUpdateVersionDirectory(string version)
    {
        var safeVersion = string.Concat(version.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_'));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CheckMechanic", "updates", safeVersion);
    }

    private static string GetCurrentAppVersionString()
    {
        try
        {
            var assembly = typeof(MainWindow).Assembly;

            // UPDATED: Prefer AssemblyInformationalVersion (SemVer string)
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (TryParseSemVerCore(informational, out var iMajor, out var iMinor, out var iPatch))
            {
                return $"{iMajor}.{iMinor}.{iPatch}";
            }

            // UPDATED: Fallback to AssemblyVersion
            var version = assembly.GetName().Version;
            if (version is not null)
            {
                var build = version.Build < 0 ? 0 : version.Build;
                return $"{version.Major}.{version.Minor}.{build}";
            }
        }
        catch
        {
        }

        // UPDATED: Fail-safe fallback for CI/local builds
        return "0.0.0";
    }

    private static bool IsNewerVersion(string candidate, string current)
    {
        // UPDATED: Strict numeric SemVer core comparison (major.minor.patch), no string compare.
        if (!TryParseSemVerCore(candidate, out var cMajor, out var cMinor, out var cPatch))
        {
            return false;
        }

        if (!TryParseSemVerCore(current, out var curMajor, out var curMinor, out var curPatch))
        {
            curMajor = 0;
            curMinor = 0;
            curPatch = 0;
        }

        if (cMajor != curMajor) return cMajor > curMajor;
        if (cMinor != curMinor) return cMinor > curMinor;
        return cPatch > curPatch;
    }

    private static bool TryParseSemVerCore(string? value, out int major, out int minor, out int patch) // UPDATED
    {
        major = 0;
        minor = 0;
        patch = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var core = value.Trim();
        var plusIdx = core.IndexOf('+');
        if (plusIdx >= 0)
        {
            core = core[..plusIdx];
        }

        var dashIdx = core.IndexOf('-');
        if (dashIdx >= 0)
        {
            core = core[..dashIdx];
        }

        var parts = core.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out major))
        {
            return false;
        }

        if (!int.TryParse(parts[1], out minor))
        {
            return false;
        }

        if (parts.Length >= 3)
        {
            if (!int.TryParse(parts[2], out patch))
            {
                return false;
            }
        }
        else
        {
            patch = 0;
        }

        return major >= 0 && minor >= 0 && patch >= 0;
    }

    private static string NormalizeSha256(string value) => value.Replace(" ", string.Empty).Trim();

    private static async Task<string> ComputeSha256Async(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream);
        return Convert.ToHexString(hash);
    }

    private async void RestartHelperButton_OnClick(object sender, RoutedEventArgs e)
    {
        TryKillHelperProcesses();
        await Task.Delay(300);
        await EnsureHelperAvailableAsync();
        await PollAllAsync(forceProfile: true);
        RefreshBindings(force: true);
    }

    private async void RestartHelperAsAdminButton_OnClick(object sender, RoutedEventArgs e)
    {
        TryKillHelperProcesses();
        await Task.Delay(300);
        if (!TryStartHelper(runAsAdmin: true))
        {
            RefreshBindings(force: true);
            return;
        }

        await Task.Delay(1200);
        await EnsureHelperAvailableAsync();
        await PollAllAsync(forceProfile: true);
        RefreshBindings(force: true);
    }

    private async void RefreshDiagnosticsButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RefreshDiagnosticsAsync();
        RefreshBindings(force: true);
    }

    private async void RefreshProfileButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RefreshProfileAsync();
        RefreshBindings(force: true);
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

    private void OpenWidgetModeButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = UiSettingsStore.Load();
            settings.WidgetModeEnabled = true;
            UiSettingsStore.Save(settings);

            var widget = new WidgetWindow();
            widget.Show();
            Application.Current.MainWindow = widget;
            _isSwitchingToWidgetMode = true;
            Close();
        }
        catch
        {
            AddLog("widget mode open failed");
            RefreshBindings(force: true);
        }
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

        RefreshBindings(force: true);
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
            StatusBadgeText = "LOCKED";   StatusBadgeBackground = new SolidColorBrush(Color.FromRgb(57, 30, 36));
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

        RefreshBindings(force: true);
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

        RefreshBindings(force: true);
    }

    private static string BuildSetupInstructions(IReadOnlyList<string>? providerErrors)
    {
        var list = providerErrors ?? Array.Empty<string>();
        if (list.Any(x => x.Contains("coretemp:TEMP_CORETEMP_SHM_NOT_FOUND", StringComparison.OrdinalIgnoreCase)))
        {
            return "Core Temp が未検出です。配布された Setup.exe を実行して Core Temp を導入後、Core Temp を起動し、Options -> Settings -> Advanced -> Enable Global Shared Memory (SNMP) を ON にして「再チェック」を実行してください。";
        }

        if (list.Any(x => x.Contains("coretemp:TEMP_CORETEMP_VALUES_UNAVAILABLE", StringComparison.OrdinalIgnoreCase)))
        {
            return "Core Temp は検出されていますが温度値を取得できません。Options -> Settings -> Advanced -> Enable Global Shared Memory (SNMP) を確認し、管理者で再試行してください。";
        }

        return "Core Temp を起動し、Options -> Settings -> Advanced -> Enable Global Shared Memory (SNMP) を ON にして「再チェック」または「管理者でSensorHelperを再起動して再試行」を実行してください。Core Temp が未導入なら Setup.exe を先に実行してください。";
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

    // --- Commit C: compute a stable hash fote (exclude LastUpdateText) ---
    private int ComputeUiHash()
    {
        var hc = new HashCode();

        // top status + gating
        hc.Add(StatusText);
        hc.Add(StatusBadgeText);
        hc.Add(RestrictionText);
        hc.Add(SetupInstructionsText);
        hc.Add(MainFeatureText);
        hc.Add((int)RestrictionVisibility);

        // key KPIs
        hc.Add(CpuTemperatureText);
        hc.Add(CpuUtilizationText);
        hc.Add(CpuKpiText);
        hc.Add(CpuAvgText);
        hc.Add(CpuDetailText);

        hc.Add(MemoryText);
        hc.Add(MemoryKpiText);
        hc.Add(MemorySubText);
        hc.Add(MemoryAvailableText);

        hc.Add(GpuText);
        hc.Add(GpuKpiText);
        hc.Add(GpuSubText);

        hc.Add(DiskText);
        hc.Add(DiskCapacityText);
        hc.Add(NetText);
        hc.Add(NetMetaText);
        hc.Add(NetDetailTooltipText);
        hc.Add(BatteryText);

        // score & radar
        hc.Add(PerfScoreText);
        hc.Add(PerfSummaryText);
        hc.Add(ScoreCpuText);
        hc.Add(ScoreMemText);
        hc.Add(ScoreDiskText);
        hc.Add(ScoreNetText);
        hc.Add(ScoreTempText);
        hc.Add(ScoreGpuText);
        hc.Add(RadarScorePoints);
        hc.Add(RadarCpuLabel);
        hc.Add(RadarMemLabel);
        hc.Add(RadarDiskLabel);
        hc.Add(RadarNetLabel);
        hc.Add(RadarThermalLabel);
        hc.Add(RadarGpuLabel);

        // chart (string paths are enough for change detection)
        hc.Add(TempChartData);
        hc.Add(CpuChartData);
        hc.Add(MemChartData);
        hc.Add(GpuChartData);
        hc.Add(DiskChartData);
        hc.Add(NetChartData);

        // temp ring
        hc.Add(TempRingArcData);
        hc.Add(TempRingCenterText);
        hc.Add(TempLatestText);
        hc.Add(TempMin60Text);
        hc.Add(TempMax60Text);
        hc.Add(TempSourceText);
        hc.Add(TempStatsText);

        // diagnostics / profiles
        hc.Add(DiagnosticsSummaryText);
        hc.Add(ProviderErrorsText);
        hc.Add(ProfileText);
        hc.Add(RankingProfileText);
        hc.Add(LocalRankingText);
        hc.Add((int)UpdateBadgeVisibility);
        hc.Add(UpdateBadgeText);
        hc.Add(UpdateBadgeTooltip);
        hc.Add(AppVersionText);

        // NOTE: LastUpdateText intentionally excluded to allow suppression
        return hc.ToHashCode();
    }

    // --- Commit B/C: no DataContext reset. Notify only if state changed. ---
    private void RefreshBindings(bool force = false)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => RefreshBindings(force));
            return;
        }

        if (!force)
        {
            var current = ComputeUiHash();
            if (_lastUiHash.HasValue && _lastUiHash.Value == current)
            {
                // no visible change -> skip rebind/re-render
                return;
            }

            _lastUiHash = current;
        }
        else
        {
            _lastUiHash = null; // next non-forced refresh will re-evaluate anyway
        }

        // Update timestamp only when we actually render
        LastUpdateText = DateTime.Now.ToString("HH:mm:ss");

        NotifyAll();
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
                    CleanupCoreTempInstallerArtifacts();
                    return p;
                }
            }
            catch
            {
            }
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "Core Temp.exe",
                UseShellExecute = true,
            });
            if (process is not null)
            {
                CleanupCoreTempInstallerArtifacts();
            }
            return process;
        }
        catch
        {
            return null;
        }
    }

    private void CleanupCoreTempInstallerArtifacts()
    {
        if (_coreTempArtifactsCleanupDone)
        {
            return;
        }

        try
        {
            var removedLinks = 0;
            foreach (var desktopDir in EnumerateDesktopCandidates())
            {
                if (!Directory.Exists(desktopDir))
                {
                    continue;
                }

                foreach (var shortcutPath in Directory.EnumerateFiles(desktopDir, "*.url", SearchOption.TopDirectoryOnly))
                {
                    if (!ShouldDeleteSuspiciousShortcut(shortcutPath))
                    {
                        continue;
                    }

                    try
                    {
                        File.Delete(shortcutPath);
                        removedLinks++;
                    }
                    catch
                    {
                    }
                }
            }

            var removedDirs = RemoveCoreTempAdDirectoryIfExists();
            if (removedLinks > 0 || removedDirs > 0)
            {
                AddLog($"core temp artifact cleanup: urls={removedLinks}, dirs={removedDirs}");
            }
        }
        catch
        {
        }
        finally
        {
            _coreTempArtifactsCleanupDone = true;
        }
    }

    private static IEnumerable<string> EnumerateDesktopCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktop))
        {
            var fullDesktop = TryGetFullPath(desktop);
            if (fullDesktop is not null && seen.Add(fullDesktop))
            {
                result.Add(fullDesktop);
            }
        }

        foreach (var envName in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
        {
            var oneDriveRoot = Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrWhiteSpace(oneDriveRoot))
            {
                continue;
            }

            var candidate = Path.Combine(oneDriveRoot, "Desktop");
            var fullPath = TryGetFullPath(candidate);
            if (fullPath is not null && seen.Add(fullPath))
            {
                result.Add(fullPath);
            }
        }

        return result;

        static string? TryGetFullPath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return null;
            }
        }
    }

    private static bool ShouldDeleteSuspiciousShortcut(string shortcutPath)
    {
        var name = Path.GetFileName(shortcutPath);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(shortcutPath);
            var recentThreshold = DateTime.UtcNow.AddHours(-36);
            var recent = info.CreationTimeUtc >= recentThreshold || info.LastWriteTimeUtc >= recentThreshold;
            if (!recent)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        string content;
        try
        {
            content = File.ReadAllText(shortcutPath);
        }
        catch
        {
            return false;
        }

        var hasAdSignature =
            content.Contains("goodgamestudios.com", StringComparison.OrdinalIgnoreCase) ||
            content.Contains(@"\Core Temp\goodgamestudios\", StringComparison.OrdinalIgnoreCase);

        if (!hasAdSignature)
        {
            return false;
        }

        return name.Contains("goodgame", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("Goodgame Empire", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("goodgamestudios", StringComparison.OrdinalIgnoreCase);
    }

    private static int RemoveCoreTempAdDirectoryIfExists()
    {
        var removed = 0;
        foreach (var baseDir in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        })
        {
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                continue;
            }

            var target = Path.Combine(baseDir, "Core Temp", "goodgamestudios");
            if (!Directory.Exists(target))
            {
                continue;
            }

            try
            {
                Directory.Delete(target, recursive: true);
                removed++;
            }
            catch
            {
            }
        }

        return removed;
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

    private sealed class UpdateManifest
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("download_url")]
        public string DownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("release_notes_url")]
        public string? ReleaseNotesUrl { get; set; }

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;
    }

    private sealed class UpdateCheckState
    {
        public DateTimeOffset LastCheckedUtc { get; set; }
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isSwitchingToWidgetMode)
        {
            return;
        }

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
