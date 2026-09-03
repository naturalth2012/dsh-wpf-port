using System.IO;
using System.Text.Json;

namespace Dsh.App.Services;

/// <summary>
/// Local JSON persistence for app preferences that should survive restarts — most notably the
/// deepseek-harness checkout directory (so the user doesn't re-pick it every launch) and the
/// gateway base URL. Stored under <c>%APPDATA%\dsh-wpf-port\settings.json</c>.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string HarnessDirectory { get; set; } = "";

    public string HostUrl { get; set; } = HarnessLauncher.DefaultBaseUrl;

    /// <summary>
    /// Composer Enter-action preference (P1-8): "queue" sends as a queued message; "alwaysSteer"
    /// sends as a steer (insert ahead of the running turn) when the session is busy. Default queue.
    /// </summary>
    public string BusyEnterAction { get; set; } = "queue";

    /// <summary>UI language code, "zh-CN" or "en" (P2-8). Persisted so the choice survives restarts.</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>"Always on top" window state (K4). Persisted so the choice survives restarts.</summary>
    public bool Topmost { get; set; } = false;

    /// <summary>
    /// Right-hand panel width in device-independent pixels (阶段3). Persisted so a user who drags
    /// the splitter to fit the trajectory ledger doesn't have to redo it every launch.
    /// 0 means "collapsed"; anything out of range falls back to the default.
    /// </summary>
    public double RightPanelWidth { get; set; } = 240;

    /// <summary>Path of the settings file (<c>%APPDATA%\dsh-wpf-port\settings.json</c>).</summary>
    public static string SettingsPath
    {
        get
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "dsh-wpf-port", "settings.json");
        }
    }

    /// <summary>Load persisted settings, or a fresh default when none exist / are malformed.</summary>
    public static AppSettings Load()
    {
        try
        {
            string path = SettingsPath;
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null) return loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                     or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            // Unreadable/corrupt settings fall back to defaults rather than blocking startup.
            // Only filesystem/serialization failures are expected; unexpected exceptions surface.
            System.Diagnostics.Debug.WriteLine($"[AppSettings] load failed: {ex.Message}");
        }
        return new AppSettings();
    }

    /// <summary>Persist current settings to disk (best-effort; never throws).</summary>
    public void Save()
    {
        try
        {
            string path = SettingsPath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                     or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            // Best-effort persistence; a write failure must not break the app.
            System.Diagnostics.Debug.WriteLine($"[AppSettings] save failed: {ex.Message}");
        }
    }
}
