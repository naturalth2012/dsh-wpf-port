using System.IO;
using System.Text.Json;

namespace Dsh.Wpf;

/// <summary>
/// Persisted UI theme preference (P2-16). Stored as a tiny JSON file next to the
/// executable so the choice survives restarts without relying on host settings.
/// </summary>
public sealed class ThemeSettings
{
    private static readonly string Path_ = Path.Combine(AppContext.BaseDirectory, "theme.json");

    public string Theme { get; set; } = "Light";

    public static ThemeSettings Load()
    {
        try
        {
            if (File.Exists(Path_) && JsonSerializer.Deserialize<ThemeSettings>(File.ReadAllText(Path_)) is { } s)
                return s;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                     or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            // Corrupt/unreadable settings fall back to defaults. Only filesystem/serialization
            // failures are expected; unexpected exceptions are left to surface.
            System.Diagnostics.Debug.WriteLine($"[ThemeSettings] load failed: {ex.Message}");
        }
        return new ThemeSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(Path_, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                     or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            // Best-effort persistence; theme still applies for the current session.
            System.Diagnostics.Debug.WriteLine($"[ThemeSettings] save failed: {ex.Message}");
        }
    }
}
