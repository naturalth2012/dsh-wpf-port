using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dsh.Contract.Methods;

namespace Dsh.App.Services;

/// <summary>
/// Local JSON cache of the last successful workspace/session list (P2-11). Lets the client
/// browse the sidebar offline (when session.list / workspace.list fail) by restoring the
/// last-known snapshot. Pure DTOs are cached, so rebuilding the tree uses the same
/// <see cref="WorkspaceTreeBuilder"/> path as a live fetch.
/// </summary>
public sealed class OfflineCache
{
    /// <summary>Serializable snapshot of one successful list fetch.</summary>
    public sealed record Snapshot
    {
        [JsonPropertyName("savedAt")]
        public long SavedAt { get; init; }

        [JsonPropertyName("workspaces")]
        public WorkspaceView[] Workspaces { get; init; } = [];

        [JsonPropertyName("sessions")]
        public SessionSummary[] Sessions { get; init; } = [];
    }

    private static readonly string DefaultPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "dsh-wpf-port", "offline-cache.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>Persist the latest successful workspace + session snapshot (best-effort).</summary>
    public static void Save(WorkspaceView[] workspaces, SessionSummary[] sessions, string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            var dir = Path.GetDirectoryName(file)!;
            Directory.CreateDirectory(dir);
            var snapshot = new Snapshot
            {
                SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Workspaces = workspaces,
                Sessions = sessions,
            };
            File.WriteAllText(file, JsonSerializer.Serialize(snapshot, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                     or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            // Cache is best-effort: a write failure must not break the live refresh. Only
            // filesystem/serialization failures are expected here; anything else (a real bug)
            // is deliberately left unhandled so it surfaces instead of being silently swallowed.
            System.Diagnostics.Debug.WriteLine($"[OfflineCache] save failed: {ex.Message}");
        }
    }

    /// <summary>Load a previously-saved snapshot, or null when none/unreadable/stale exists.</summary>
    public static Snapshot? TryLoad(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            if (!File.Exists(file)) return null;
            var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(file), Options);
            if (snapshot is null) return null;
            // P2-11: discard stale snapshots so an old cache is never shown as "current" data.
            var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - snapshot.SavedAt;
            if (age > MaxAgeMs) return null;
            return snapshot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                     or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            // Cache is best-effort: an unreadable/corrupt cache is treated as "no cache".
            System.Diagnostics.Debug.WriteLine($"[OfflineCache] load failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Snapshots older than this are treated as absent (stale offline data).</summary>
    private static readonly long MaxAgeMs = (long)TimeSpan.FromDays(7).TotalMilliseconds;

    // ---- J7: per-session history replay cache ----
    // The sidebar snapshot above only holds the workspace/session tree. To let the transcript
    // also be replayed offline (J7), we additionally cache the last successful per-session
    // history page as raw JSON events. It lives in its own file so the tree snapshot and the
    // (potentially large) transcript payload don't churn each other.

    private static string HistoryPath(string sessionId)
    {
        var safe = string.Join('_', sessionId.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "dsh-wpf-port", $"offline-history-{safe}.json");
    }

    /// <summary>Serializable snapshot of one session's history page.</summary>
    public sealed record HistorySnapshot
    {
        [JsonPropertyName("savedAt")]
        public long SavedAt { get; init; }

        [JsonPropertyName("events")]
        public JsonElement[] Events { get; init; } = [];
    }

    /// <summary>Persist the last successful history page for a session (best-effort).</summary>
    public static void SaveHistory(string sessionId, System.Text.Json.JsonElement[] events)
    {
        try
        {
            var file = HistoryPath(sessionId);
            var dir = Path.GetDirectoryName(file)!;
            Directory.CreateDirectory(dir);
            var snapshot = new HistorySnapshot
            {
                SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Events = events,
            };
            File.WriteAllText(file, JsonSerializer.Serialize(snapshot, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                     or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            // Cache is best-effort: an unreadable/corrupt cache is treated as "no cache".
            System.Diagnostics.Debug.WriteLine($"[OfflineCache] save failed: {ex.Message}");
        }
    }

    /// <summary>Load a session's cached history, or null when none/unreadable/stale exists.</summary>
    public static HistorySnapshot? TryLoadHistory(string sessionId)
    {
        try
        {
            var file = HistoryPath(sessionId);
            if (!File.Exists(file)) return null;
            var snapshot = JsonSerializer.Deserialize<HistorySnapshot>(File.ReadAllText(file), Options);
            if (snapshot is null) return null;
            var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - snapshot.SavedAt;
            if (age > MaxAgeMs) return null;
            return snapshot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                     or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            // Cache is best-effort: an unreadable/corrupt cache is treated as "no cache".
            System.Diagnostics.Debug.WriteLine($"[OfflineCache] load failed: {ex.Message}");
            return null;
        }
    }
}
