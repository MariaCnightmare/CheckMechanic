using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CheckMechanic.Shared;

namespace CheckMechanic.Desktop;

public partial class WidgetWindow : Window
{
    private const string HelperBaseUrl = "http://127.0.0.1:17805";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(1.5) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly Queue<double?> _cpuHistory = new();
    private readonly Queue<double?> _memHistory = new();
    private readonly Queue<double?> _diskHistory = new();
    private readonly Queue<double?> _netHistory = new();
    private bool _isSwitchingToFullUi;

    public string CpuTemperatureText { get; set; } = "--";
    public string CpuMemText { get; set; } = "-- / --";
    public string PerfScoreText { get; set; } = "--";
    public string LastUpdateText { get; set; } = "--";
    public string StatusBadgeText { get; set; } = "STARTING";
    public Brush StatusBadgeBackground { get; set; } = new SolidColorBrush(Color.FromRgb(24, 40, 56));
    public Brush StatusBadgeBorder { get; set; } = new SolidColorBrush(Color.FromRgb(39, 70, 91));

    public WidgetWindow()
    {
        InitializeComponent();
        DataContext = this;

        Loaded += WidgetWindow_OnLoaded;
        Closing += WidgetWindow_OnClosing;
        LocationChanged += WidgetWindow_OnLocationChanged;
        _timer.Tick += WidgetTimer_OnTick;
    }

    private async void WidgetWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyWidgetSettingsOnLoad();
        await EnsureHelperAvailableAsync();
        await PollTelemetryAsync();
        _timer.Start();
    }

    private void ApplyWidgetSettingsOnLoad()
    {
        var settings = UiSettingsStore.Load();
        Topmost = settings.WidgetTopmost;
        TopmostMenuItem.IsChecked = Topmost;

        if (settings.WidgetLeft.HasValue && settings.WidgetTop.HasValue)
        {
            Left = settings.WidgetLeft.Value;
            Top = settings.WidgetTop.Value;
        }
        else
        {
            ResetToDefaultPosition();
        }
    }

    private async void WidgetTimer_OnTick(object? sender, EventArgs e)
    {
        if (!_pollGate.Wait(0))
        {
            return;
        }

        try
        {
            await PollTelemetryAsync();
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private async Task PollTelemetryAsync()
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{HelperBaseUrl}/v1/telemetry");
            if (!response.IsSuccessStatusCode)
            {
                SetStatus("DEGRADED");
                CpuTemperatureText = "--";
                CpuMemText = "-- / --";
                PerfScoreText = "N/A";
                LastUpdateText = DateTime.Now.ToString("HH:mm:ss");
                NotifyAll();
                return;
            }

            var raw = await response.Content.ReadAsStringAsync();
            var payload = JsonSerializer.Deserialize<TelemetryResponse>(raw, JsonOptions) ?? new TelemetryResponse();

            CpuTemperatureText = payload.Cpu.TempC is double temp ? $"{temp:F1} °C" : "N/A";
            var cpuText = payload.Cpu.UtilPercent is double cu ? $"{cu:F1}%" : "--";
            var memText = payload.Memory.UtilPercent is double mu ? $"{mu:F1}%" : "--";
            CpuMemText = $"{cpuText} / {memText}";

            double? diskTotal = (payload.Disk.ReadBps.HasValue || payload.Disk.WriteBps.HasValue)
                ? payload.Disk.ReadBps.GetValueOrDefault() + payload.Disk.WriteBps.GetValueOrDefault()
                : null;
            double? netTotal = (payload.Net.RecvBps.HasValue || payload.Net.SentBps.HasValue)
                ? payload.Net.RecvBps.GetValueOrDefault() + payload.Net.SentBps.GetValueOrDefault()
                : null;
            AppendWithLimit(_cpuHistory, payload.Cpu.UtilPercent, 60);
            AppendWithLimit(_memHistory, payload.Memory.UtilPercent, 60);
            AppendWithLimit(_diskHistory, diskTotal, 60);
            AppendWithLimit(_netHistory, netTotal, 60);

            var score = PerfScoreCalculator.Calculate(payload.Cpu.TempC, _cpuHistory.ToList(), _memHistory.ToList(), _diskHistory.ToList(), _netHistory.ToList());
            PerfScoreText = score.IsLocked ? "N/A (LOCKED)" : $"{score.Score} ({score.Grade})";

            if (payload.Cpu.TempC is null)
            {
                SetStatus("LOCKED");
            }
            else
            {
                SetStatus("OK");
            }

            LastUpdateText = DateTime.Now.ToString("HH:mm:ss");
            NotifyAll();
        }
        catch
        {
            SetStatus("DEGRADED");
            CpuTemperatureText = "--";
            CpuMemText = "-- / --";
            PerfScoreText = "N/A";
            LastUpdateText = DateTime.Now.ToString("HH:mm:ss");
            NotifyAll();
        }
    }

    private static void AppendWithLimit(Queue<double?> queue, double? value, int max)
    {
        queue.Enqueue(value);
        while (queue.Count > max)
        {
            queue.Dequeue();
        }
    }

    private async Task<bool> CheckHealthAsync()
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{HelperBaseUrl}/health");
            if (!response.IsSuccessStatusCode)
            {
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

    private async Task EnsureHelperAvailableAsync()
    {
        if (await CheckHealthAsync())
        {
            return;
        }

        if (await IsPortOpenAsync())
        {
            return;
        }

        if (!TryStartHelper())
        {
            return;
        }

        for (var i = 0; i < 8; i++)
        {
            await Task.Delay(500);
            if (await CheckHealthAsync())
            {
                return;
            }
        }
    }

    private bool TryStartHelper()
    {
        var startInfo = ResolveHelperStartInfo();
        if (startInfo is null)
        {
            return false;
        }

        try
        {
            Process.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ProcessStartInfo? ResolveHelperStartInfo()
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
                UseShellExecute = false,
                CreateNoWindow = true,
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
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(dllPath),
            };
        }

        return null;
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

    private void WidgetRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            OpenFullUi();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
            }
        }
    }

    private void MenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ContextMenu is null)
        {
            return;
        }

        ContextMenu.PlacementTarget = this;
        ContextMenu.IsOpen = true;
    }

    private void WidgetWindow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        TopmostMenuItem.IsChecked = Topmost;
    }

    private void OpenFullUiMenuItem_OnClick(object sender, RoutedEventArgs e) => OpenFullUi();

    private void OpenFullUi()
    {
        var settings = UiSettingsStore.Load();
        settings.WidgetModeEnabled = false;
        settings.WidgetTopmost = Topmost;
        settings.WidgetLeft = Left;
        settings.WidgetTop = Top;
        UiSettingsStore.Save(settings);

        var main = new MainWindow();
        main.Show();
        Application.Current.MainWindow = main;
        _isSwitchingToFullUi = true;
        Close();
    }

    private void TopmostMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item)
        {
            return;
        }

        Topmost = item.IsChecked;
        SaveWidgetSettings();
    }

    private void ResetPositionMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        ResetToDefaultPosition();
        SaveWidgetSettings();
    }

    private void CloseMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void WidgetWindow_OnLocationChanged(object? sender, EventArgs e)
    {
        SaveWidgetSettings();
    }

    private void WidgetWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_isSwitchingToFullUi)
        {
            var settings = UiSettingsStore.Load();
            settings.WidgetModeEnabled = true;
            settings.WidgetTopmost = Topmost;
            settings.WidgetLeft = Left;
            settings.WidgetTop = Top;
            UiSettingsStore.Save(settings);
        }
    }

    private void ResetToDefaultPosition()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + 16;
        Top = area.Bottom - Height - 16;
    }

    private void SaveWidgetSettings()
    {
        try
        {
            var settings = UiSettingsStore.Load();
            settings.WidgetModeEnabled = true;
            settings.WidgetTopmost = Topmost;
            settings.WidgetLeft = Left;
            settings.WidgetTop = Top;
            UiSettingsStore.Save(settings);
        }
        catch
        {
        }
    }

    private void SetStatus(string mode)
    {
        if (mode == "OK")
        {
            StatusBadgeText = "OK";
            StatusBadgeBackground = new SolidColorBrush(Color.FromRgb(24, 58, 44));
            StatusBadgeBorder = new SolidColorBrush(Color.FromRgb(61, 132, 101));
            return;
        }

        if (mode == "LOCKED")
        {
            StatusBadgeText = "LOCKED";
            StatusBadgeBackground = new SolidColorBrush(Color.FromRgb(57, 30, 36));
            StatusBadgeBorder = new SolidColorBrush(Color.FromRgb(147, 78, 91));
            return;
        }

        StatusBadgeText = "DEGRADED";
        StatusBadgeBackground = new SolidColorBrush(Color.FromRgb(60, 52, 26));
        StatusBadgeBorder = new SolidColorBrush(Color.FromRgb(156, 136, 71));
    }

    private void NotifyAll()
    {
        Dispatcher.Invoke(() =>
        {
            DataContext = null;
            DataContext = this;
            TopmostMenuItem.IsChecked = Topmost;
        });
    }
}
