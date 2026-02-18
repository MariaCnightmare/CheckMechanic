using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CheckMechanic.Shared;

namespace CheckMechanic.Desktop;

public partial class MainWindow : Window
{
    private const string HelperBaseUrl = "http://127.0.0.1:17805";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(1.5) };
    private readonly DispatcherTimer _timer;

    public string CpuTemperatureText { get; set; } = "未取得";
    public string StatusText { get; set; } = "⚠ 起動中";
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
                AddLog($"telemetry HTTP {(int)response.StatusCode}");
                RefreshBindings();
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            var payload = JsonSerializer.Deserialize<TelemetryResponse>(json, JsonOptions);
            if (payload is null)
            {
                SetStatus("❌ Telemetry parse failed");
                CpuTemperatureText = "未取得";
                AddLog("telemetry parse failed");
                RefreshBindings();
                return;
            }

            if (payload.Cpu.TempC is double temp)
            {
                CpuTemperatureText = $"{temp:F1} °C";
                SetStatus("✅ OK");
                AddLog($"temp={temp:F1}C label={payload.Cpu.Label}");
            }
            else
            {
                CpuTemperatureText = "未取得";
                SetStatus("⚠ 温度取得不可");
                AddLog($"temperature unavailable: {payload.Cpu.Error ?? "unknown"}");
            }

            RefreshBindings();
        }
        catch (HttpRequestException ex)
        {
            CpuTemperatureText = "未取得";
            SetStatus("❌ SensorHelper 未接続");
            AddLog(SanitizeError(ex.Message));
            RefreshBindings();
        }
        catch (Exception)
        {
            CpuTemperatureText = "未取得";
            SetStatus("❌ 取得失敗");
            AddLog("telemetry request failed");
            RefreshBindings();
        }
    }

    private bool TryStartHelper()
    {
        var startInfo = ResolveHelperStartInfo();
        if (startInfo is null)
        {
            AddLog("helper binary not found");
            return false;
        }

        try
        {
            Process.Start(startInfo);
            AddLog($"helper started: {startInfo.FileName} {startInfo.Arguments}".Trim());
            return true;
        }
        catch (Exception)
        {
            AddLog("helper start failed");
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

    private async void ReconnectButton_OnClick(object sender, RoutedEventArgs e)
    {
        await EnsureHelperAvailableAsync();
    }

    private async void RestartHelperButton_OnClick(object sender, RoutedEventArgs e)
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

        await Task.Delay(300);
        await EnsureHelperAvailableAsync();
    }

    private void SetStatus(string text)
    {
        StatusText = text;
        RefreshBindings();
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
