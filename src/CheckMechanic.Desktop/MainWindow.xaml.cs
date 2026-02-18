using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CheckMechanic.Shared;
using FlaUI.Core;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA3;
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
    public string SetupInstructionsText { get; set; } = "Core Temp を起動し、Options -> Settings -> Advanced -> Enable Global Shared Memory (SNMP) を ON にして「再チェック」を実行してください。";
    public string MainFeatureText { get; set; } = "制限モード: 主要機能は利用できません。診断情報を確認してください。";
    public string DiagnosticSensorsText { get; set; } = "(未取得)";
    public bool ExperimentalAutoEnableConsent { get; set; } = false;
    public bool CloseCoreTempOnExitConsent { get; set; } = false;
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
        Closing += MainWindow_OnClosing;
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

            if (payload.Cpu.TempC is double temp)
            {
                CpuTemperatureText = $"{temp:F1} °C";
                CpuUtilizationText = payload.Cpu.UtilPercent is double utilVal ? $"CPU使用率: {utilVal:F1}%" : "CPU使用率: 未取得";
                SetStatus("✅ OK");
                SetRestrictedMode(false, string.Empty, string.Empty);
                AddLog($"temp={temp:F1}C util={payload.Cpu.UtilPercent?.ToString("F1") ?? "n/a"} provider={payload.Cpu.ProviderUsed ?? "unknown"} label={payload.Cpu.Label}");
            }
            else
            {
                CpuTemperatureText = "未取得";
                CpuUtilizationText = payload.Cpu.UtilPercent is double utilVal ? $"CPU使用率: {utilVal:F1}%" : "CPU使用率: 未取得";
                SetStatus("❌ 必須要件未達（温度取得不可）");
                SetRestrictedMode(
                    true,
                    "必須要件未達: 温度取得が必要です。管理者で再試行してください。",
                    BuildSetupInstructions(payload.Cpu.ProviderErrors));
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
            SetRestrictedMode(true, "必須要件未達: 温度取得が必要です。", "Core Temp を起動した状態で再チェックしてください。");
            AddLog(SanitizeError(ex.Message));
            RefreshBindings();
        }
        catch (Exception)
        {
            CpuTemperatureText = "未取得";
            SetStatus("❌ 取得失敗");
            SetRestrictedMode(true, "必須要件未達: 温度取得が必要です。", "Core Temp を起動した状態で再チェックしてください。");
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
        await PollTelemetryAsync();
        await RefreshDiagnosticsAsync();
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

    private async void AutoEnableCoreTempSharedMemoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!ExperimentalAutoEnableConsent)
        {
            SetStatus("⚠ 実験機能の同意が必要です");
            AddLog("experimental auto-enable rejected: consent required");
            return;
        }

        var proc = LaunchOrActivateCoreTemp();
        if (proc is null)
        {
            SetStatus("❌ Core Temp を起動できませんでした");
            AddLog("experimental auto-enable failed: core temp not found");
            return;
        }

        try
        {
            await Task.Delay(800);
            var automationResult = await Task.Run(() => TryEnableCoreTempSharedMemoryByFlaUi(proc.Id));
            foreach (var line in automationResult.Diagnostics)
            {
                AddLog($"[auto-coretemp] {line}");
            }
            AddLog($"experimental auto-enable flow completed: success={automationResult.Success}");
            await Task.Delay(300);
            await RecheckAfterAutomationAsync();
            var validation = await ValidateCoreTempAutomationSuccessAsync();
            if (validation.Success)
            {
                SetStatus("✅ Core Temp 共有メモリ設定を確認しました");
            }
            else
            {
                SetStatus("❌ 自動設定後も要件未達");
                if (!automationResult.Success)
                {
                    AddLog("[auto-coretemp] FlaUI automation did not complete all steps");
                }
                AddLog($"[auto-coretemp] validation failed: {validation.Reason}");
                SetupInstructionsText = "Core Temp: Options -> Settings -> Advanced -> Enable Global Shared Memory (SNMP) を ON にして、再チェックしてください。";
            }
        }
        catch (Exception ex)
        {
            AddLog($"experimental auto-enable failed: {ex.GetType().Name}: {ex.Message}");
            SetStatus("❌ 自動設定に失敗。手動手順を実施してください。");
            SetupInstructionsText = "Core Temp: Options -> Settings -> Advanced -> Enable Global Shared Memory (SNMP) を ON にして、再チェックしてください。";
            RefreshBindings();
        }
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

    private void SetRestrictedMode(bool restricted, string reason, string setupInstructions)
    {
        _isRestrictedMode = restricted;
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

    private async Task RecheckAfterAutomationAsync()
    {
        await EnsureHelperAvailableAsync();
        await PollTelemetryAsync();
        await RefreshDiagnosticsAsync();
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
                // try next candidate
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
            // best effort
        }
    }

    private static CoreTempAutomationResult TryEnableCoreTempSharedMemoryByFlaUi(int processId)
    {
        var diagnostics = new List<string>();
        void LogDiag(string text) => diagnostics.Add(text);
        try
        {
            using var automation = new UIA3Automation();
            using var app = FlaUI.Core.Application.Attach(processId);
            var mainWindow = app.GetMainWindow(automation, TimeSpan.FromSeconds(8));
            if (mainWindow is null)
            {
                LogDiag("main_window found=false");
                return new CoreTempAutomationResult(false, diagnostics);
            }

            LogDiag($"main_window title='{mainWindow.Title}' class='{mainWindow.ClassName}' pid={processId}");
            mainWindow.Focus();
            Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.ALT, FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_O);
            Thread.Sleep(200);
            Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_S);
            Thread.Sleep(600);

            var settingsWindow = app.GetAllTopLevelWindows(automation)
                .FirstOrDefault(w => w.Title.Contains("Settings", StringComparison.OrdinalIgnoreCase))
                ?? mainWindow;
            LogDiag($"settings_window title='{settingsWindow.Title}' class='{settingsWindow.ClassName}' pid={settingsWindow.Properties.ProcessId.ValueOrDefault}");

            var tabItems = settingsWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem))
                .Select(t => t.Name)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToList();
            LogDiag($"tab_items=[{string.Join(", ", tabItems)}]");

            var advanced = settingsWindow.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.TabItem).And(cf.ByName("Advanced")));
            var advancedSelected = false;
            if (advanced is not null)
            {
                var sel = advanced.Patterns.SelectionItem.PatternOrDefault;
                if (sel is not null)
                {
                    sel.Select();
                    advancedSelected = true;
                    LogDiag("advanced_tab selection_item.select used");
                }
                else
                {
                    advanced.Focus();
                    advanced.Click();
                    advancedSelected = true;
                    LogDiag("advanced_tab click used");
                }
            }
            else
            {
                LogDiag("advanced_tab not found, fallback keyboard navigation");
                FallbackEnableSharedMemoryByKeyboard(LogDiag);
            }
            Thread.Sleep(300);

            var checkboxNames = settingsWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox))
                .Select(c => c.Name)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Take(20)
                .ToList();
            LogDiag($"checkbox_names_top20=[{string.Join(", ", checkboxNames)}]");

            var checkbox = settingsWindow.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.CheckBox).And(cf.ByName("Enable Global Shared Memory (SNMP)")));
            if (checkbox is null)
            {
                checkbox = settingsWindow.FindFirstDescendant(cf =>
                    cf.ByControlType(ControlType.CheckBox).And(cf.ByName("Enable Global Shared Memory")));
            }
            if (checkbox is null)
            {
                checkbox = settingsWindow.FindFirstDescendant(cf =>
                    cf.ByControlType(ControlType.CheckBox).And(cf.ByName("SNMP")));
            }

            if (checkbox is null)
            {
                LogDiag("snmp_checkbox found=false");
                if (!advancedSelected)
                {
                    FallbackEnableSharedMemoryByKeyboard(LogDiag);
                }
                return new CoreTempAutomationResult(false, diagnostics);
            }
            LogDiag($"snmp_checkbox found=true name='{checkbox.Name}'");

            var toggleState = checkbox.Patterns.Toggle.PatternOrDefault?.ToggleState;
            LogDiag($"snmp_checkbox toggle_state_before={toggleState?.ToString() ?? "n/a"}");
            var togglePattern = checkbox.Patterns.Toggle.PatternOrDefault;
            if (togglePattern is not null)
            {
                if (togglePattern.ToggleState != ToggleState.On)
                {
                    togglePattern.Toggle();
                    LogDiag("snmp_checkbox toggle_pattern.toggle used");
                }
                else
                {
                    LogDiag("snmp_checkbox already on");
                }
            }
            else if (toggleState != ToggleState.On)
            {
                checkbox.Click();
                LogDiag("snmp_checkbox click fallback used");
            }
            Thread.Sleep(150);
            var toggleStateAfter = checkbox.Patterns.Toggle.PatternOrDefault?.ToggleState;
            LogDiag($"snmp_checkbox toggle_state_after={toggleStateAfter?.ToString() ?? "n/a"}");

            var applyOrOk = settingsWindow.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Button).And(cf.ByName("Apply")))
                ?? settingsWindow.FindFirstDescendant(cf =>
                    cf.ByControlType(ControlType.Button).And(cf.ByName("OK")));
            if (applyOrOk is null)
            {
                LogDiag("apply_or_ok found=false");
                return new CoreTempAutomationResult(false, diagnostics);
            }

            var invokePattern = applyOrOk.Patterns.Invoke.PatternOrDefault;
            if (invokePattern is not null)
            {
                invokePattern.Invoke();
                LogDiag("apply_or_ok invoke_pattern used");
            }
            else
            {
                applyOrOk.Click();
                LogDiag("apply_or_ok click fallback used");
            }

            return new CoreTempAutomationResult(true, diagnostics);
        }
        catch (Exception ex)
        {
            LogDiag($"exception={ex.GetType().Name}: {ex.Message}");
            return new CoreTempAutomationResult(false, diagnostics);
        }
    }

    private static void FallbackEnableSharedMemoryByKeyboard(Action<string> log)
    {
        log("keyboard_fallback: Ctrl+Tab x4, Tab x10, Space, Enter");
        for (var i = 0; i < 4; i++)
        {
            Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL, FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
            Thread.Sleep(70);
        }
        for (var i = 0; i < 10; i++)
        {
            Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
            Thread.Sleep(40);
        }
        Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.SPACE);
        Thread.Sleep(80);
        Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ENTER);
    }

    private async Task<(bool Success, string Reason)> ValidateCoreTempAutomationSuccessAsync()
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{HelperBaseUrl}/v1/telemetry");
            if (!response.IsSuccessStatusCode)
            {
                return (false, $"telemetry HTTP {(int)response.StatusCode}");
            }
            var json = await response.Content.ReadAsStringAsync();
            var payload = JsonSerializer.Deserialize<TelemetryResponse>(json, JsonOptions);
            if (payload is null)
            {
                return (false, "telemetry parse failed");
            }

            var providerErrors = payload.Cpu.ProviderErrors ?? new List<string>();
            var hasShmNotFound = providerErrors.Any(x =>
                x.Equals("coretemp:TEMP_CORETEMP_SHM_NOT_FOUND", StringComparison.OrdinalIgnoreCase));
            var providerOk = string.Equals(payload.Cpu.ProviderUsed, "coretemp", StringComparison.OrdinalIgnoreCase);
            var tempOk = payload.Cpu.TempC is double;

            if (!hasShmNotFound && providerOk && tempOk)
            {
                return (true, "coretemp ready");
            }

            return (false, $"provider={payload.Cpu.ProviderUsed ?? "null"} temp={(payload.Cpu.TempC?.ToString("F1") ?? "null")} shm_not_found={hasShmNotFound}");
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
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
            // best effort shutdown only
        }
    }

    private sealed record CoreTempAutomationResult(bool Success, List<string> Diagnostics);
}
