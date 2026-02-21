using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CheckMechanic.Desktop;

public sealed class DesktopUiSettings
{
    public bool CloseCoreTempOnExit { get; set; } = true;
    public bool WidgetModeEnabled { get; set; }
    public bool WidgetTopmost { get; set; } = true;
    public double? WidgetLeft { get; set; }
    public double? WidgetTop { get; set; }
    public double WidgetOpacity { get; set; } = 0.85;
    public bool WidgetClickThrough { get; set; }
}

internal static class UiSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static DesktopUiSettings Load()
    {
        try
        {
            var path = GetUiSettingsFilePath();
            if (!File.Exists(path))
            {
                return new DesktopUiSettings();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<DesktopUiSettings>(json, JsonOptions) ?? new DesktopUiSettings();
        }
        catch
        {
            return new DesktopUiSettings();
        }
    }

    public static void Save(DesktopUiSettings settings)
    {
        var path = GetUiSettingsFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
    }

    public static string GetAppDataDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "CheckMechanic");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".checkmechanic");
    }

    private static string GetUiSettingsFilePath()
    {
        var dir = GetAppDataDirectory();
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "desktop_ui_settings.json");
    }
}
