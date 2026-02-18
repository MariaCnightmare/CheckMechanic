using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
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

    private string _lastTelemetryJson = "{}";
    private string _lastProfileJson = "{}";
    private int _pollCount;
    private bool _optInConsent;

    public string CpuTemperatureText { get; set; } = "未取得";
    public string CpuUtilizationText { get; set; } = "CPU使用率: 未取得";
    public string MemoryText { get; set; } = "未取得";
    public string DiskText { get; set; } = "未取得";
    public string NetText { get; set; } = "未取得";
    public string PerfScoreText { get; set; } = "-- (N/A)";
    public string StatusText { get; set; } = "⚠ 起動中";
    public string RestrictionText { get; set; } = "必須要件未達: 温度取得が必要です。";
    public string SetupInstructionsText { get; set; } = "Core Temp を起動し、Options -> Settings -> Advanced -> Enable Global Shared Memory (SNMP) を ON にして「再チェック」を実行してください。";
    public string MainFeatureText { get; set; } = "制限モード: 主要機能は利用できません。診断情報を確認してください。";
    public string DiagnosticSensorsText { get; set; } = "(未取得)";
    public string ProfileText { get; set; } = "(未取得)";
    public string RankingProfileText { get; set; } = "Opt-in をONにするとカテゴリを生成します。";
    public string TempChartData { get; set; } = string.Empty;
    public string CpuChartData { get; set; } = string.Empty;
    public string MemChartData { get; set; } = string.Empty;
    public bool CloseCoreTempOnExitConsent { get; set; }
    public ObservableCollection<string> Logs { get; } = new();

    public bool OptInConsent
    {
        get => _optInConsent;
        set
        {
            _optInConsent = value;
            UpdateRankingPreview();
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
            MemoryText = BuildMemoryText(payload.Memory);
            DiskText = $"R {FormatRate(payload.Disk.ReadBps)} / W {FormatRate(payload.Disk.WriteBps)}";
            NetText = $"↓ {FormatRate(payload.Net.RecvBps)} / ↑ {FormatRate(payload.Net.SentBps)}";

            if (payload.Cpu.TempC is double temp)
            {
                CpuTemperatureText = $"{temp:F1} °C";
                SetStatus("✅ OK");
                SetRestrictedMode(false, string.Empty, string.Empty);
                AddLog($"temp={temp:F1}C util={payload.Cpu.UtilPercent?.ToString("F1") ?? "n/a"} provider={payload.Cpu.ProviderUsed ?? "unknown"} label={payload.Cpu.Label}");
            }
            else
            {
                CpuTemperatureText = "未取得";
                SetStatus("❌ 必須要件未達（温度取得不可）");
                SetRestrictedMode(
                    true,
                    "必須要件未達: 温度取得が必要です。管理者で再試行してください。",
                    BuildSetupInstructions(payload.Cpu.ProviderErrors));
                var providerErrors = payload.Cpu.ProviderErrors?.Count > 0 ? string.Join(",", payload.Cpu.ProviderErrors) : "none";
                AddLog($"temperature unavailable: code={payload.Cpu.ErrorCode ?? "unknown"} detail={payload.Cpu.Error ?? "unknown"} util={payload.Cpu.UtilPercent?.ToString("F1") ?? "n/a"} provider_errors={providerErrors}");
            }

            UpdateScore(payload);
            AppendHistory(payload.Cpu.TempC, payload.Cpu.UtilPercent, payload.Memory.UtilPercent);
            RefreshChartData();
            RefreshBindings();
        }
        catch (HttpRequestException ex)
        {
            CpuTemperatureText = "未取得";
            SetStatus("❌ SensorHelper 未接続");
            SetRestrictedMode(true, "必須要件未達: 温度取得が必要です。", "Core Temp を起動した状態で再チェックしてください。");
            AddLog(SanitizeError(ex.Message));
            RefreshBindings();
        }
        catch
        {
            CpuTemperatureText = "未取得";
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
            UpdateRankingPreview();
        }
        catch
        {
            ProfileText = "{\"error\":\"profile_unavailable\"}";
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
        var cpu = NormalizePercent(payload.Cpu.UtilPercent);
        var mem = NormalizePercent(payload.Memory.UtilPercent);
        var disk = NormalizeRate(payload.Disk.ReadBps.GetValueOrDefault() + payload.Disk.WriteBps.GetValueOrDefault(), 300 * 1024 * 1024);
        var net = NormalizeRate(payload.Net.RecvBps.GetValueOrDefault() + payload.Net.SentBps.GetValueOrDefault(), 100 * 1024 * 1024);
        var tempStress = NormalizeTempStress(payload.Cpu.TempC);

        var score =
            (cpu * 0.30) +
            (mem * 0.20) +
            (disk * 0.20) +
            (net * 0.10) +
            (tempStress * 0.20);

        var final = Math.Clamp((int)Math.Round(score * 100), 0, 100);
        var rank = final >= 90 ? "S" : final >= 75 ? "A" : final >= 55 ? "B" : "C";
        PerfScoreText = $"{final} ({rank})";
    }

    private static double NormalizePercent(double? value)
    {
        if (!value.HasValue) return 0;
        return Math.Clamp(value.Value / 100.0, 0, 1);
    }

    private static double NormalizeRate(double value, double max)
    {
        if (max <= 0) return 0;
        return Math.Clamp(value / max, 0, 1);
    }

    private static double NormalizeTempStress(double? temp)
    {
        if (!temp.HasValue) return 0;
        if (temp.Value <= 45) return 0.1;
        if (temp.Value <= 60) return 0.3;
        if (temp.Value <= 75) return 0.6;
        if (temp.Value <= 90) return 0.85;
        return 1.0;
    }

    private void AppendHistory(double? temp, double? cpu, double? mem)
    {
        AppendWithLimit(_tempHistory, temp, 40);
        AppendWithLimit(_cpuHistory, cpu, 40);
        AppendWithLimit(_memHistory, mem, 40);
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
        TempChartData = BuildSparklinePath(_tempHistory, 960, 140, 30, 100);
        CpuChartData = BuildSparklinePath(_cpuHistory, 960, 140, 0, 100);
        MemChartData = BuildSparklinePath(_memHistory, 960, 140, 0, 100);
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
        RefreshBindings();
    }

    private void SetRestrictedMode(bool restricted, string reason, string setupInstructions)
    {
        RestrictionText = restricted ? reason : string.Empty;
        SetupInstructionsText = restricted ? setupInstructions : string.Empty;
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
