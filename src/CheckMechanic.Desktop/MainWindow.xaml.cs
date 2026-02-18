using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CheckMechanic.Shared;
using Microsoft.Win32;

namespace CheckMechanic.Desktop;

public partial class MainWindow : Window
{
    private const string HelperBaseUrl = "http://127.0.0.1:17805";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(1.5) };
    private readonly DispatcherTimer _timer;
    private string _lastTelemetryJson = "{}";
    private bool _isRestrictedMode = true;

    public string CpuTemperatureText { get; set; } = "未取得";
    public string CpuUtilizationText { get; set; } = "CPU使用率: 未取得";
    public string StatusText { get; set; } = "⚠ 起動中";
    public string RestrictionText { get; set; } = "必須要件未達: 温度取得が必要です。";
    public string MainFeatureText { get; set; } = "制限モード: 主要機能は利用できません。診断情報を確認してください。";
    public string DiagnosticSensorsText { get; set; } = "(未取得)";
    public ObservableCollection<string> Logs { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.5),
        };
        _timer.Tick += async (_, _) => await PollTelemetryAsync();

        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await EnsureHelperAvailableAsync();
        await PollTelemetryAsync();
        await RefreshDiagnosticsAsync();
        _timer.Start();
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
        catch (Exception)
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
                SetRestrictedMode(true, "必須要件未達: 温度取得が必要です。");
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
                SetRestrictedMode(true, "必須要件未達: 温度取得が必要です。");
                AddLog("telemetry parse failed");
                RefreshBindings();
                return;
            }

            if (payload.Cpu.TempC is double temp)
            {
                CpuTemperatureText = $"{temp:F1} °C";
                CpuUtilizationText = payload.Cpu.UtilPercent is double utilVal ? $"CPU使用率: {utilVal:F1}%" : "CPU使用率: 未取得";
                SetStatus("✅ OK");
                SetRestrictedMode(false, string.Empty);
                AddLog($"temp={temp:F1}C util={payload.Cpu.UtilPercent?.ToString("F1") ?? "n/a"} provider={payload.Cpu.ProviderUsed ?? "unknown"} label={payload.Cpu.Label}");
            }
            else
            {
                CpuTemperatureText = "未取得";
                CpuUtilizationText = payload.Cpu.UtilPercent is double utilVal ? $"CPU使用率: {utilVal:F1}%" : "CPU使用率: 未取得";
                SetStatus("❌ 必須要件未達（温度取得不可）");
                SetRestrictedMode(true, "必須要件未達: 温度取得が必要です。管理者で再試行してください。");
                var providerErrors = payload.Cpu.ProviderErrors?.Count > 0 ? string.Join(",", payload.Cpu.ProviderErrors) : "none";
                AddLog($"temperature unavailable: code={payload.Cpu.ErrorCode ?? "unknown"} detail={payload.Cpu.Error ?? "unknown"} util={payload.Cpu.UtilPercent?.ToString("F1") ?? "n/a"} provider_errors={providerErrors}");
                await RefreshDiagnosticsAsync();
            }

            RefreshBindings();
        }
        catch (HttpRequestException ex)
        {
            CpuTemperatureText = "未取得";
            SetStatus("❌ SensorHelper 未接続");
            SetRestrictedMode(true, "必須要件未達: 温度取得が必要です。");
            AddLog(SanitizeError(ex.Message));
            RefreshBindings();
        }
        catch (Exception)
        {
            CpuTemperatureText = "未取得";
            SetStatus("❌ 取得失敗");
            SetRestrictedMode(true, "必須要件未達: 温度取得が必要です。");
            AddLog("telemetry request failed");
            RefreshBindings();
        }
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
        catch (Exception)
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

    private async void RestartHelperButton_OnClick(object sender, RoutedEventArgs e)
    {
        TryKillHelperProcesses();
        await Task.Delay(300);
        await EnsureHelperAvailableAsync();
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
        await PollTelemetryAsync();
    }

    private async void RefreshDiagnosticsButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RefreshDiagnosticsAsync();
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
                telemetry_raw = TryParseJsonOrRaw(_lastTelemetryJson),
                sensors_raw = TryParseJsonOrRaw(DiagnosticSensorsText),
                logs = Logs.ToList(),
            };
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
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

    private void SetRestrictedMode(bool restricted, string reason)
    {
        _isRestrictedMode = restricted;
        RestrictionText = restricted ? reason : string.Empty;
        MainFeatureText = restricted
            ? "制限モード: 温度取得が確認できるまで主要画面は利用不可です。診断情報を確認してください。"
            : "通常モード: 温度取得を確認済みです。";
    }

    private void AddLog(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        Logs.Insert(0, line);
        while (Logs.Count > 50)
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
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
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
}
