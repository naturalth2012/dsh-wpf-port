namespace Dsh.App;

partial class SessionFold
{
    /// <summary>
    /// One step in the session trajectory (event ledger).
    ///
    /// Field set aligned with the native-web <c>ui-trajectory</c> contract so the WPF view can
    /// render the same information (turn/request grouping, timing, error state, call pairing).
    /// The four original positional parameters are kept first and the new ones are optional, so
    /// existing call sites / tests constructing <c>new TrajectoryStep(i, kind, seq, time, text)</c>
    /// keep compiling unchanged.
    /// </summary>
    /// <param name="Index">Monotonic position in event order (0-based).</param>
    /// <param name="Kind">
    /// Step type, matching the native kinds: <c>user</c>, <c>message</c> (assistant),
    /// <c>tool</c>, <c>context</c> (injected input / config change), <c>compacted</c>,
    /// <c>system</c>, <c>subtool</c>. <c>todo</c> is a WPF extension (todo/write snapshots).
    /// Note the native product treats <c>turn</c> as a GROUPING level, not a step kind — turn
    /// boundaries are carried by <see cref="TurnIndex"/> instead of emitting a step.
    /// </param>
    /// <param name="Seq">Originating event sequence number.</param>
    /// <param name="Time">Originating event timestamp (host clock).</param>
    /// <param name="Text">Short display summary (truncated — see MaxStepText).</param>
    /// <param name="TurnIndex">1-based turn this step belongs to (native "Turn N" grouping).</param>
    /// <param name="RequestIndex">1-based assistant request within the turn (native "Request #N").</param>
    /// <param name="StartedAt">Actual start timestamp when known, else the event time.</param>
    /// <param name="DurationMs">Wall-clock duration in ms when the payload reports one.</param>
    /// <param name="IsError">True when the step represents a failure (e.g. a tool error result).</param>
    /// <param name="CallId">Tool call id, used to pair a tool/call with its tool/result.</param>
    /// <param name="Payload">
    /// Detached copy of the originating event <c>data</c> object, kept so a details pane can show
    /// the full payload/result on demand (the native ledger's Payload/Result tabs). It is stored
    /// as a CLONED element (safe beyond the source JsonDocument's lifetime) and is null when the
    /// payload was absent, too large (see MaxStepPayloadChars), or for high-frequency step kinds
    /// where the detail value does not justify the copy — the truncated <see cref="Text"/> is
    /// then the only content available.
    /// </param>
    /// <param name="InputTokens">Prompt tokens (native <c>input</c>).</param>
    /// <param name="CacheReadTokens">Prompt tokens served from the provider cache (native <c>cacheRead</c>).</param>
    /// <param name="CacheWriteTokens">Prompt tokens written into the provider cache (native <c>cacheWrite</c>).</param>
    /// <param name="OutputTokens">Completion tokens (native <c>output</c>).</param>
    /// <param name="ThinkTokens">Reasoning tokens (native <c>think</c>).</param>
    /// <param name="TtftMs">
    /// Time to first token, derived the same way the native product derives it
    /// (firstTokenTime − stepStartTime) when the payload carries both, else null.
    /// </param>
    public sealed record TrajectoryStep(
        int Index,
        string Kind,
        long Seq,
        long Time,
        string Text,
        int? TurnIndex = null,
        int? RequestIndex = null,
        long? StartedAt = null,
        long? DurationMs = null,
        bool IsError = false,
        string? CallId = null,
        System.Text.Json.JsonElement? Payload = null,
        long? InputTokens = null,
        long? CacheReadTokens = null,
        long? CacheWriteTokens = null,
        long? OutputTokens = null,
        long? ThinkTokens = null,
        long? TtftMs = null);
}
