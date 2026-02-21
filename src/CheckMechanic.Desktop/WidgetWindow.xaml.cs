using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Interop;
using CheckMechanic.Shared;

namespace CheckMechanic.Desktop;

public partial class WidgetWindow : Window, INotifyPropertyChanged
{
    private const string HelperBaseUrl = "http://127.0.0.1:17805";
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WmHotKey = 0x0312;
    private const int HotKeyIdToggleClickThrough = 0x434D31;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkW = 0x57;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(1.5) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly Queue<double?> _tempHistory = new();
    private readonly Queue<double?> _cpuHistory = new();
    private readonly Queue<double?> _memHistory = new();
    private readonly Queue<double?> _diskHistory = new();
    private readonly Queue<double?> _netHistory = new();
    private bool _isSwitchingToFullUi;
    private bool _isHovering;
    private bool _clickThroughEnabled;
    private bool _hotKeyRegistered;
    private double _widgetOpacitySetting = 0.85;
    public event PropertyChangedEventHandler? PropertyChanged;

    public string CpuTemperatureText { get; set; } = "--";
    public string CpuMemText { get; set; } = "-- / --";
    public string PerfScoreText { get; set; } = "--";
    public string LastUpdateText { get; set; } = "--";
    public string StatusBadgeText { get; set; } = "STARTING";
    public Brush StatusBadgeBackground { get; set; } = new SolidColorBrush(Color.FromRgb(24, 40, 56));
    public Brush StatusBadgeBorder { get; set; } = new SolidColorBrush(Color.FromRgb(39, 70, 91));
    public string TempSparkPath { get; set; } = string.Empty;
    public string CpuSparkPath { get; set; } = string.Empty;
    public double WidgetBackgroundOpacity { get; set; } = 0.85;
    public double WidgetOpacitySetting
    {
        get => _widgetOpacitySetting;
        set => _widgetOpacitySetting = Math.Clamp(value, 0.65, 0.95);
    }

    public string WidgetOpacityLabel => $"{Math.Round(WidgetOpacitySetting * 100):F0}%";

    public WidgetWindow()
    {
        InitializeComponent();
        DataContext = this;

        Loaded += WidgetWindow_OnLoaded;
        SourceInitialized += WidgetWindow_OnSourceInitialized;
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
        WidgetOpacitySetting = settings.WidgetOpacity;
        _clickThroughEnabled = settings.WidgetClickThrough;
        ClickThroughMenuItem.IsChecked = _clickThroughEnabled;
        OpacitySlider.Value = WidgetOpacitySetting;
        UpdateBackgroundOpacity();
        ApplyClickThrough();

        if (settings.WidgetLeft.HasValue && settings.WidgetTop.HasValue)
        {
            Left = settings.WidgetLeft.Value;
            Top = settings.WidgetTop.Value;
        }
        else
        {
            ResetToDefaultPosition();
        }

        ClampToWorkArea();
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
                TempSparkPath = string.Empty;
                CpuSparkPath = string.Empty;
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
            AppendWithLimit(_tempHistory, payload.Cpu.TempC, 30);
            AppendWithLimit(_cpuHistory, payload.Cpu.UtilPercent, 60);
            AppendWithLimit(_memHistory, payload.Memory.UtilPercent, 60);
            AppendWithLimit(_diskHistory, diskTotal, 60);
            AppendWithLimit(_netHistory, netTotal, 60);
            UpdateSparklinePaths();

            var cpuValues = _cpuHistory.Where(x => x.HasValue).Select(x => x!.Value).ToList();
            var memValues = _memHistory.Where(x => x.HasValue).Select(x => x!.Value).ToList();
            var score = PerfScoreCalculator.Calculate(payload.Cpu.TempC, cpuValues, memValues, _diskHistory.ToList(), _netHistory.ToList());
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
            TempSparkPath = string.Empty;
            CpuSparkPath = string.Empty;
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
        ClickThroughMenuItem.IsChecked = _clickThroughEnabled;
        OpacitySlider.Value = WidgetOpacitySetting;
    }

    private void OpenFullUiMenuItem_OnClick(object sender, RoutedEventArgs e) => OpenFullUi();

    private void OpenFullUi()
    {
        var settings = UiSettingsStore.Load();
        settings.WidgetModeEnabled = false;
        settings.WidgetTopmost = Topmost;
        settings.WidgetLeft = Left;
        settings.WidgetTop = Top;
        settings.WidgetOpacity = WidgetOpacitySetting;
        settings.WidgetClickThrough = _clickThroughEnabled;
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

    private void ClickThroughMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item)
        {
            return;
        }

        _clickThroughEnabled = item.IsChecked;
        ApplyClickThrough();
        SaveWidgetSettings();
    }

    private void OpacitySlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        WidgetOpacitySetting = e.NewValue;
        UpdateBackgroundOpacity();
        SaveWidgetSettings();
        NotifyAll();
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
        ClampToWorkArea();
        SaveWidgetSettings();
    }

    private void WidgetWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        UnregisterGlobalHotKey();

        if (!_isSwitchingToFullUi)
        {
            var settings = UiSettingsStore.Load();
            settings.WidgetModeEnabled = true;
            settings.WidgetTopmost = Topmost;
            settings.WidgetLeft = Left;
            settings.WidgetTop = Top;
            settings.WidgetOpacity = WidgetOpacitySetting;
            settings.WidgetClickThrough = _clickThroughEnabled;
            UiSettingsStore.Save(settings);
        }
    }

    private void ResetToDefaultPosition()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + 16;
        Top = area.Bottom - Height - 16;
    }

    private void ClampToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        var right = area.Right - Width;
        var bottom = area.Bottom - Height;
        Left = Math.Max(area.Left, Math.Min(Left, right));
        Top = Math.Max(area.Top, Math.Min(Top, bottom));
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
            settings.WidgetOpacity = WidgetOpacitySetting;
            settings.WidgetClickThrough = _clickThroughEnabled;
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
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(NotifyAll);
            return;
        }

        TopmostMenuItem.IsChecked = Topmost;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private void WidgetWindow_OnSourceInitialized(object? sender, EventArgs e)
    {
        RegisterGlobalHotKey();
        ApplyClickThrough();
    }

    private void WidgetWindow_MouseEnter(object sender, MouseEventArgs e)
    {
        _isHovering = true;
        UpdateBackgroundOpacity();
        NotifyAll();
    }

    private void WidgetWindow_MouseLeave(object sender, MouseEventArgs e)
    {
        _isHovering = false;
        UpdateBackgroundOpacity();
        NotifyAll();
    }

    private void UpdateBackgroundOpacity()
    {
        var boost = _isHovering ? 0.10 : 0.0;
        WidgetBackgroundOpacity = Math.Min(0.95, WidgetOpacitySetting + boost);
    }

    private void ApplyClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLong(handle, GwlExStyle);
        var nextStyle = _clickThroughEnabled
            ? (exStyle | WsExTransparent)
            : (exStyle & ~WsExTransparent);
        if (nextStyle != exStyle)
        {
            SetWindowLong(handle, GwlExStyle, nextStyle);
        }
    }

    private void RegisterGlobalHotKey()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || _hotKeyRegistered)
        {
            return;
        }

        if (RegisterHotKey(handle, HotKeyIdToggleClickThrough, ModControl | ModShift, VkW))
        {
            _hotKeyRegistered = true;
            var source = HwndSource.FromHwnd(handle);
            source?.AddHook(WndProc);
        }
    }

    private void UnregisterGlobalHotKey()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !_hotKeyRegistered)
        {
            return;
        }

        try
        {
            var source = HwndSource.FromHwnd(handle);
            source?.RemoveHook(WndProc);
            UnregisterHotKey(handle, HotKeyIdToggleClickThrough);
        }
        catch
        {
        }
        finally
        {
            _hotKeyRegistered = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == HotKeyIdToggleClickThrough)
        {
            _clickThroughEnabled = !_clickThroughEnabled;
            ClickThroughMenuItem.IsChecked = _clickThroughEnabled;
            ApplyClickThrough();
            SaveWidgetSettings();
            NotifyAll();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void UpdateSparklinePaths()
    {
        const double width = 152;
        const double height = 20;

        var tempValues = _tempHistory.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        var tempMin = tempValues.Count > 0 ? tempValues.Min() : 30;
        var tempMax = tempValues.Count > 0 ? tempValues.Max() : 100;
        if (Math.Abs(tempMax - tempMin) < 6)
        {
            tempMax = tempMin + 6;
        }

        TempSparkPath = BuildSparklinePath(_tempHistory, width, height, tempMin, tempMax);
        CpuSparkPath = BuildSparklinePath(_cpuHistory.TakeLast(30), width, height, 0, 100);
    }

    private static string BuildSparklinePath(IEnumerable<double?> values, double width, double height, double minY, double maxY)
    {
        var list = values.ToList();
        if (list.Count < 2 || maxY <= minY)
        {
            return string.Empty;
        }

        var stepX = width / Math.Max(1, list.Count - 1);
        var sb = new StringBuilder();
        var hasPoint = false;

        for (var i = 0; i < list.Count; i++)
        {
            var value = list[i];
            if (!value.HasValue)
            {
                continue;
            }

            var normalized = Math.Clamp((value.Value - minY) / (maxY - minY), 0, 1);
            var x = i * stepX;
            var y = height - (normalized * height);
            if (!hasPoint)
            {
                sb.Append($"M {x:F1},{y:F1} ");
                hasPoint = true;
            }
            else
            {
                sb.Append($"L {x:F1},{y:F1} ");
            }
        }

        return sb.ToString();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
