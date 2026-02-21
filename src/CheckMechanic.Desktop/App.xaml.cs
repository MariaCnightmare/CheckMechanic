using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace CheckMechanic.Desktop;

public partial class App : Application
{
    private static readonly string CrashLogPath = Path.Combine(Path.GetTempPath(), "checkmechanic_desktop_crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LogCrash("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", args.Exception);
        };

        base.OnStartup(e);

        var settings = UiSettingsStore.Load();
        Window window = settings.WidgetModeEnabled
            ? new WidgetWindow()
            : new MainWindow();
        MainWindow = window;
        window.Show();
    }

    // App.xaml の DispatcherUnhandledException="App_DispatcherUnhandledException" から呼ばれる
    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        LogCrash("Application.DispatcherUnhandledException", args.Exception);

        // ログは残すが、壊れた状態で続行しない（クラッシュさせる）
        args.Handled = false;
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {source}");
            if (ex is not null)
            {
                sb.AppendLine(ex.ToString());
            }
            sb.AppendLine();
            File.AppendAllText(CrashLogPath, sb.ToString());
        }
        catch
        {
            // クラッシュハンドラ内では例外を飲み込む（無限ループ防止）
        }
    }
}
