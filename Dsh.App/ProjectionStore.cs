using System.Collections.Concurrent;
using System.Text.Json;
using Dsh.Contract.Frames;

namespace Dsh.App;

/// <summary>
/// Per-session projection repository, mirroring the Web client's
/// <c>ProjectionValueStore</c>: holds the latest value of each projection key for each
/// session, applying higher-seq-wins so an out-of-order frame never regresses a value.
/// Cleared on generation teardown (the connection layer owns that call).
/// </summary>
public sealed class ProjectionStore
{
    // The host projects values in camelCase (wire contract), so a case-sensitive default
    // options would drop every `required` property and throw JsonException. Mirror the
    // transport codec's naming policy for projection-value deserialization.
    private static readonly JsonSerializerOptions _valueOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Cell>> _sessions = new();

    private sealed record Cell(long Seq, object? Value);

    /// <summary>Apply one <c>session/projection</c> frame; returns true if the value was committed.</summary>
    public bool Apply(SessionProjectionFrame frame)
    {
        var keys = _sessions.GetOrAdd(frame.SessionId, _ => new());

        // higher-seq-wins: ignore a frame that is not strictly newer than what we hold.
        if (keys.TryGetValue(frame.Key, out var existing) && existing.Seq >= frame.Seq)
        {
            return false;
        }

        keys[frame.Key] = new Cell(frame.Seq, frame.Value);
        return true;
    }

    /// <summary>
    /// Seed one projection value directly (e.g. from the history tail page's
    /// <c>projections.values</c> baseline). Applies the same higher-seq-wins rule as
    /// <see cref="Apply"/>. Returns true if the value was committed.
    /// </summary>
    public bool ApplyValue(string sessionId, string key, JsonElement value, long seq)
    {
        var keys = _sessions.GetOrAdd(sessionId, _ => new());
        if (keys.TryGetValue(key, out var existing) && existing.Seq >= seq)
        {
            return false;
        }
        keys[key] = new Cell(seq, value);
        return true;
    }

    /// <summary>Read the current projection value for a key; null if never received.</summary>
    public object? Get(string sessionId, string key)
    {
        if (_sessions.TryGetValue(sessionId, out var keys) && keys.TryGetValue(key, out var cell))
        {
            return cell.Value;
        }
        return null;
    }

    /// <summary>Read the current projection value deserialized to a strong type.</summary>
    public T? Get<T>(string sessionId, string key)
    {
        var raw = Get(sessionId, key);
        if (raw is T typed) return typed;
        if (raw is JsonElement el) return el.Deserialize<T>(_valueOptions);
        return default;
    }

    /// <summary>Read the latest seq for a key (0 if never received); used for resubscription alignment.</summary>
    public long GetSeq(string sessionId, string key)
    {
        if (_sessions.TryGetValue(sessionId, out var keys) && keys.TryGetValue(key, out var cell))
        {
            return cell.Seq;
        }
        return 0;
    }

    /// <summary>Drop all state for one session (session removed).</summary>
    public void RemoveSession(string sessionId) => _sessions.TryRemove(sessionId, out _);

    /// <summary>Drop all state (generation teardown).</summary>
    public void Clear() => _sessions.Clear();
}
