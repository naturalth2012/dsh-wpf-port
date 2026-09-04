using System.Text;
using System.Text.Json;
using Dsh.App.Services;
using Dsh.Contract.Methods;

namespace Dsh.App;

/// <summary>
/// Folds raw <c>SessionEvent</c> payloads into a surface transcript, mirroring the Web
/// client's shared fold. Handles token-level streaming (text/reasoning deltas), message
/// finalization, and a tool-call tree keyed by <c>callId</c>.
/// </summary>
public sealed partial class SessionFold
{
    private readonly StringBuilder _textBuffer = new();
    private readonly StringBuilder _reasoningBuffer = new();
    private string? _activeRole;

    /// <summary>
    /// P0-1: index of the row whose Text/Reasoning are still only available in the streaming
    /// buffers (not yet copied into the row), or -1 when nothing is pending. AppendChunk sets it;
    /// <see cref="MaterializePendingRow"/> clears it after flushing the buffers into the row.
    /// </summary>
    private int _pendingRowIndex = -1;

    /// <summary>
    /// P0-1: flush the streaming buffers into the pending row so <see cref="Rows"/> reflects the
    /// latest text. Call before anything reads row Text/Reasoning (rendering, compacting). Costs
    /// one O(n) copy instead of one per chunk. Safe to call repeatedly (no-op when nothing pending).
    /// </summary>
    public void MaterializePendingRow()
    {
        int idx = _pendingRowIndex;
        if (idx < 0) return;
        _pendingRowIndex = -1;
        if (idx >= Rows.Count) return;
        Rows[idx] = Rows[idx] with
        {
            Text = _textBuffer.ToString(),
            Reasoning = _reasoningBuffer.Length == 0 ? null : _reasoningBuffer.ToString(),
        };
    }

    /// <summary>Tool-call views keyed by callId, captured from tool/call so tool/result can
    /// derive produced files from the mutation tool's own <c>locations</c> (P2-5).</summary>
    private readonly Dictionary<string, JsonElement> _callViews = new();

    /// <summary>Accumulated produced-file paths for the active turn, first-seen order (P2-5).</summary>
    private readonly List<DeliverableItem> _deliverables = new();

    /// <summary>Accumulated surface rows; a row is (role, text, reasoning?, toolCall?).</summary>
    public List<Row> Rows { get; } = new();

    /// <summary>Latest whole-list todo snapshot (todo/write event).</summary>
    public IReadOnlyList<TodoItem> Todos { get; private set; } = [];

    /// <summary>
    /// Files produced (written/edited) by mutation tools in the current turn (P2-5).
    /// Derived from tool/result's follow-along <c>locations</c> by render intent (a diff
    /// card, or a generic card whose <c>kind</c> is <c>edit</c>) — never from the closing
    /// prose. First-seen order, deduped, scoped to the active turn.
    /// </summary>
    /// <remarks>
    /// Deliberately an expression body over the live <c>_deliverables</c> list (same shape as
    /// <see cref="Trajectory"/>): consumers detect mutations via <see cref="DeliverablesVersion"/>
    /// and snapshot with <c>ToArray()</c>. An earlier auto-property + self-assignment pattern is
    /// exactly what kept the produced-files bar from ever updating.
    /// </remarks>
    public IReadOnlyList<DeliverableItem> Deliverables => _deliverables;

    /// <summary>
    /// Monotonic counter bumped whenever <c>_deliverables</c> is mutated (append / clear).
    /// Consumers MUST use this to detect changes: <see cref="Deliverables"/> returns the SAME
    /// live <c>List</c> instance on every call, so a view-model setter that assigns it sees no
    /// reference change and never raises PropertyChanged — the produced-files bar then kept
    /// whatever it first rendered and never gained the new file buttons (the same class of bug
    /// as the trajectory timeline, fixed with the same version-publish pattern).
    /// </summary>
    public int DeliverablesVersion { get; private set; }

    // DeliverableItem, TrajectoryStep — extracted to SessionFold.DeliverableItem.cs / SessionFold.TrajectoryStep.cs

    /// <summary>Steps in event order, for the trajectory/replay view (P2-6).</summary>
    public IReadOnlyList<TrajectoryStep> Trajectory => _trajectory;

    private readonly List<TrajectoryStep> _trajectory = new();

    /// <summary>
    /// Monotonic counter bumped whenever <c>_trajectory</c> is mutated (append / trim / clear).
    /// Consumers MUST use this to detect changes: <see cref="Trajectory"/> returns the SAME live
    /// <c>List</c> instance on every call, so an INotifyPropertyChanged setter that assigns it
    /// sees no reference change and never raises PropertyChanged — which is exactly why the WPF
    /// trajectory view stayed permanently blank while the native-web one was fully populated.
    /// </summary>
    public int TrajectoryVersion { get; private set; }

    /// <summary>Max characters of payload text stored per step. The tooltip renders at most 240
    /// chars, so storing more across up to <c>MaxTrajectorySteps</c> steps is pure memory waste.</summary>
    private const int MaxStepText = 240;

    /// <summary>Upper bound on the raw JSON retained per step for the details pane. A single tool
    /// result can legitimately carry megabytes of output; cloning all of it across up to
    /// <c>MaxTrajectorySteps</c> steps would balloon memory, so oversized payloads keep only the
    /// truncated <see cref="TrajectoryStep.Text"/> summary.</summary>
    private const int MaxStepPayloadChars = 8192;

    /// <summary>
    /// Detach a copy of the event payload so it stays valid after the source JsonDocument is
    /// disposed (<c>Clone()</c> produces an independent document). Returns null when absent,
    /// oversized, or malformed — the truncated text summary is enough in that case.
    /// </summary>
    private static System.Text.Json.JsonElement? ClonePayload(System.Text.Json.JsonElement payload, string type)
    {
        // assistant/chunk is the highest-frequency event in the whole stream (thousands per
        // second) and its payload is a tiny {type,index,text} delta whose detail value is nil —
        // copying one per chunk would be pure overhead on the hot path.
        if (type == "assistant/chunk") return null;
        if (payload.ValueKind is not (System.Text.Json.JsonValueKind.Object
                                      or System.Text.Json.JsonValueKind.Array)) return null;
        try
        {
            // GetRawText is the only way to measure size before copying; it allocates a temporary
            // string, which is acceptable because this runs once per step (not per chunk).
            if (payload.GetRawText().Length > MaxStepPayloadChars) return null;
            return payload.Clone();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException
                                     or ObjectDisposedException)
        {
            // A payload that cannot be measured/cloned is treated as "no payload" (best effort).
            System.Diagnostics.Debug.WriteLine($"[SessionFold] ClonePayload skipped: {ex.Message}");
            return null;
        }
    }

    // ── Turn / request grouping (native parity) ───────────────────────────────────────────
    // The native product groups the ledger as "Turn N" → "Request #N" → steps. `turn` is a
    // GROUPING level there, not a step kind, so turn/start advances the counter instead of
    // emitting a step, and every subsequent step is tagged with the current indices.
    private int _currentTurnIndex;      // 0 until the first turn/start; 1-based once started.
    private int _currentRequestIndex;   // 0 until the first assistant request of the turn.

    // StreamingStats — extracted to SessionFold.StreamingStats.cs

    private long _chunkCount;
    private long _appendMicros;
    private int _peakRows;
    private readonly System.Diagnostics.Stopwatch _appendWatch = new();

    /// <summary>Snapshot and reset the streaming telemetry counters (thread-unsafe; call from the
    /// same thread that folds). Used by the UI's periodic sampler.</summary>
    public StreamingStats TakeStreamingStats()
    {
        var stats = new StreamingStats
        {
            ChunkCount = _chunkCount,
            AppendMicros = _appendMicros,
            PeakRows = _peakRows,
        };
        _chunkCount = 0;
        _appendMicros = 0;
        _peakRows = Rows.Count;
        return stats;
    }

    // Row, ToolCallNode, DiffLine — extracted to SessionFold.Row.cs / SessionFold.ToolCallNode.cs

    /// <summary>Fold one session event. Returns true if the surface changed.
    /// Per-event isolation: a malformed/unexpected payload MUST NOT abort the fold loop.
    /// The host can legitimately emit session-metadata events the surface does not render
    /// (e.g. permission/preset, sandbox/mode, approval/policy, session/end-seed) — those
    /// are acknowledged silently rather than swallowed by the default branch.</summary>
    public bool Fold(JsonElement eventElement) => Fold(eventElement, null);

    /// <summary>
    /// Fold one session event, with the host-computed render intent that travels ALONGSIDE the
    /// event (the <c>session/event</c> mux frame's top-level <c>view</c> slot, or a history
    /// entry's <c>view</c>) — never inside the event's <c>data</c>.
    /// <para>
    /// The view is what a mutation tool's presenter produced for THIS event (presentCall for a
    /// tool/call, presentResult for a tool/result); it is the only reliable source of "which
    /// files did this edit touch" for the produced-files bar. Wire archaeology: the fold used to
    /// look for a <c>view</c> nested in <c>data</c>, which the host never sends, so
    /// <c>_callViews</c> stayed empty and the produced-files bar never showed a single file.
    /// </para>
    /// </summary>
    /// <param name="eventElement">The SessionEvent ({ type, seq, time, data }).</param>
    /// <param name="view">
    /// Host render intent accompanying this event, when the transport provided one (mux frame
    /// <c>view</c> slot / history entry <c>view</c>). Null when absent (non-tool events, or
    /// transports/tests that carry none).
    /// </param>
    public bool Fold(JsonElement eventElement, JsonElement? view)
    {
        try
        {
            string type = eventElement.ValueKind == JsonValueKind.Object
                && eventElement.TryGetProperty("type", out var t)
                && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? ""
                : "";

            // SessionEvent is { type, seq, time, data, ... }; the payload lives under `data`.
            // Fall back to the element itself when `data` is absent (tests / minimal shapes).
            JsonElement payload = eventElement.TryGetProperty("data", out var data)
                ? data
                : eventElement;

            // The event seq (outer envelope field) is used to tag produced files (P2-5).
            long seq = eventElement.TryGetProperty("seq", out var s) && s.ValueKind == JsonValueKind.Number
                ? s.GetInt64()
                : 0;

            // P2-6: record a trajectory step for surface-bearing events (independent of how
            // the fold renders Rows, so replay can show context/compacted steps too).
            long eventTime = eventElement.TryGetProperty("time", out var tm) && tm.ValueKind == JsonValueKind.Number
                ? tm.GetInt64()
                : 0;
            AppendTrajectoryStep(type, payload, seq, eventTime);

            bool changed = type switch
            {
                "assistant/chunk" => AppendChunk(payload),
                "assistant/message" => FinalizeFromMessage(payload, eventTime),
                "user/message" => FinalizeThenAddUser(payload, eventTime),
                // The render intent rides NEXT TO the event on the wire (mux frame / history
                // entry), so it is threaded into exactly the two handlers that consume it.
                "tool/call" => AddToolCall(payload, view),
                "tool/result" => SettleToolResult(payload, seq, view),
                "turn/start" => StartTurn(payload),
                "turn/end" => EndTurn(payload),
                "todo/write" => HandleTodoWrite(payload),
                "compaction/summary" => HandleCompaction(payload),
                "llm/retry" => HandleRetry(payload),
                // Session-metadata events: host emits these to record config changes. They
                // are part of the timeline but produce no chat surface. Acknowledge them so
                // they don't pollute diagnostics as "unrecognized".
                "permission/preset" => true,
                "sandbox/mode" => true,
                "approval/policy" => true,
                "session/end-seed" => true,
                _ => false,
            };
            // A1: hard caps on surface rows and trajectory steps so a long-running single
            // session cannot grow memory unboundedly. The UI renders only the tail window
            // (MaxTranscriptRenderLines in the VM), so trimming the head is safe — the tail
            // window math in SyncFoldToUi uses Rows.Count, which shrinks consistently.
            TrimRows();
            TrimTrajectory();
            return changed;
        }
        catch (Exception ex)
        {
            // Per the discipline in os/06-os-self-audit.md: any single malformed event
            // MUST NOT propagate up and tear down the entire fold / read loop. Surface the
            // error in the chat as a non-fatal row and continue with the next event.
            Rows.Add(new Row("error", Localization.Format("Fold.FoldError", ex.Message)));
            TrimRows();
            return true;
        }
    }

    /// <summary>Max surface rows kept in memory (A1). Larger than the UI render window so the
    /// tail 2000 rows always render fully, but bounded so a long session can't balloon.</summary>
    private const int MaxRows = 3000;

    /// <summary>Max trajectory steps kept for the replay view (A1).</summary>
    private const int MaxTrajectorySteps = 3000;

    /// <summary>
    /// P2-7: hysteresis slack. TrimRows does an O(n) <c>RemoveRange(0, …)</c> block move, and it
    /// runs on EVERY Fold — at MaxRows=3000 that is a 3000-element shift per event. With slack we
    /// only pay that cost once every <c>TrimSlack</c> rows instead of on every single event.
    /// </summary>
    private const int TrimSlack = 500;

    private void TrimRows()
    {
        // Only trim once we exceed MaxRows + slack, and then cut all the way back down to MaxRows
        // (not just to the threshold) so we don't immediately re-enter the expensive path.
        if (Rows.Count > MaxRows + TrimSlack)
        {
            Rows.RemoveRange(0, Rows.Count - MaxRows);
        }
    }

    /// <summary>
    /// Drop surface message rows that ended up empty — no text, reasoning, or tool — so they
    /// don't render as thin blank bubbles. Called only after a fold is fully built (history
    /// replay), never during live streaming: <see cref="AppendChunk"/> backfills <c>Rows[^1]</c>,
    /// so removing the empty assistant placeholder mid-stream would mis-target that backfill.
    /// </summary>
    public void Compact()
    {
        // P0-1: Compact reads row Text, so commit any in-flight streamed text first.
        MaterializePendingRow();
        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            var r = Rows[i];
            if (r.Role is "assistant" or "user" or "context" &&
                string.IsNullOrWhiteSpace(r.Text) &&
                r.Reasoning is null &&
                r.Tool is null)
            {
                Rows.RemoveAt(i);
            }
        }
    }

    private void TrimTrajectory()
    {
        if (_trajectory.Count > MaxTrajectorySteps)
        {
            _trajectory.RemoveRange(0, _trajectory.Count - MaxTrajectorySteps);
            TrajectoryVersion++;   // head dropped (indices shifted): force the UI to re-read.
        }
    }

    /// <summary>
    /// Reset per-session state so a freshly-loaded history starts clean: clears the
    /// trajectory timeline, the tool-view cache and the produced-file accumulator
    /// (P2-5/P2-6). Call before folding a new session's history.
    /// </summary>
    public void Reset()
    {
        // P0-1: a fresh session starts with no in-flight streamed row.
        _pendingRowIndex = -1;
        _trajectory.Clear();
        // Reset the turn/request grouping so the new session's ledger starts at Turn 1.
        _currentTurnIndex = 0;
        _currentRequestIndex = 0;
        TrajectoryVersion++;   // publish the cleared timeline to the UI.
        _callViews.Clear();
        _deliverables.Clear();
        // Publish the cleared list to the UI (version bump, NOT a self-assignment — see the
        // DeliverablesVersion doc above).
        DeliverablesVersion++;
    }

    /// <summary>
    /// Append one trajectory step for a surface event (P2-6). Steps accumulate in event
    /// order with a monotonic <see cref="TrajectoryStep.Index"/>, giving a UI the raw
    /// timeline for step-by-step replay.
    /// </summary>
    private void AppendTrajectoryStep(string type, JsonElement payload, long seq, long time)
    {
        // ── Grouping bookkeeping (native parity: turn is a group, not a step) ──────────────
        switch (type)
        {
            case "turn/start":
                // Advance the turn counter and reset the per-turn request counter. No step is
                // emitted: the native ledger derives "Turn N" headers from TurnIndex.
                _currentTurnIndex++;
                _currentRequestIndex = 0;
                TrajectoryVersion++;   // grouping changed even though no step was added
                return;
            case "turn/end":
                return;                // turn boundary only; no step
            case "assistant/message":
                _currentRequestIndex++;
                break;
        }

        string kind;
        string text;
        switch (type)
        {
            case "user/message":
                // Native splits user/message by source.kind: genuine user input is USER, while
                // host-injected content (history replay, context injection) is CONTEXT.
                kind = IsUserOrigin(payload) ? "user" : "context";
                // Host wire: payload.content is a ContentBlock[] (not a bare string).
                // `payload` here is the event's `data` object, so dig into `.content`.
                text = ExtractTextFromContent(GetContent(payload), out _);
                break;
            case "assistant/message":
            {
                kind = "message";   // native kind for assistant output (rendered as ASSISTANT)
                // Show the REAL message content. The previous code stored the literal
                // "assistant message", which made every assistant step indistinguishable and
                // useless next to the native-web timeline (which renders the actual content).
                // Real wire nests the blocks under data.message.content, while GetContent only
                // looks at data.content — so fall back through `message` when it isn't an array.
                var contentRoot = GetContent(payload);
                if (contentRoot.ValueKind != JsonValueKind.Array
                    && payload.TryGetProperty("message", out var msgEl)
                    && msgEl.ValueKind == JsonValueKind.Object)
                {
                    contentRoot = GetContent(msgEl);
                }
                text = ExtractTextFromContent(contentRoot, out var reasoning);
                if (string.IsNullOrWhiteSpace(text) && reasoning is not null) text = reasoning;
                if (string.IsNullOrWhiteSpace(text)) text = "assistant message";
                break;
            }

            case "assistant/chunk":
            {
                kind = "message";   // native kind for assistant output (rendered as ASSISTANT)
                // Real wire: data.chunk = { type, index, text } — this is exactly the shape
                // AppendChunk reads. Record the delta so streaming steps show their content.
                text = payload.TryGetProperty("chunk", out var chunkEl)
                       && chunkEl.ValueKind == JsonValueKind.Object
                       && chunkEl.TryGetProperty("text", out var delta)
                       && delta.ValueKind == JsonValueKind.String
                    ? delta.GetString() ?? ""
                    : "";
                if (string.IsNullOrWhiteSpace(text)) text = "assistant chunk";
                break;
            }

            case "tool/call":
            case "tool/result":
            {
                kind = "tool";
                var toolName = payload.TryGetProperty("name", out var tn) && tn.ValueKind == JsonValueKind.String
                    ? tn.GetString() ?? ""
                    : "";
                // Identify the invocation: tool/call carries arguments, tool/result carries output.
                var detail = FirstString(payload, "arguments", "args", "input", "output", "text", "result");
                text = toolName.Length == 0 ? type
                     : detail.Length == 0 ? toolName
                     : $"{toolName}: {detail}";
                break;
            }

            // NOTE: turn/start and turn/end are handled above as GROUPING signals (they advance
            // TurnIndex and emit no step), matching the native product where "Turn N" is a group
            // header derived from the step's turn index rather than a step of its own.

            // ---- Timeline-only events -------------------------------------------------
            // These produce no chat surface, but the native-web trajectory DOES show them.
            // Previously they all fell through `default: return` and vanished from the WPF
            // timeline, which is why it looked empty next to the web one.
            case "todo/write":
                kind = "todo";
                text = DescribeTodos(payload);
                break;

            case "compaction/start":
            case "compaction/summary":
            case "compaction/end":
                kind = "compacted";   // native kind for context compaction (rendered as COMPACTED)
                text = FirstString(payload, "summary", "text", "content") is { Length: > 0 } summary
                    ? summary
                    : "compaction";
                break;

            case "llm/retry":
                kind = "message";
                text = "retry: " + (FirstString(payload, "reason", "error", "message") is { Length: > 0 } why
                    ? why : "llm retry");
                break;

            case "permission/preset":
                kind = "context";
                text = "permission -> " + (FirstString(payload, "preset", "name", "value") is { Length: > 0 } pv
                    ? pv : "changed");
                break;

            case "sandbox/mode":
                kind = "context";
                text = "sandbox -> " + (FirstString(payload, "mode", "name", "value") is { Length: > 0 } sv
                    ? sv : "changed");
                break;

            case "approval/policy":
                kind = "context";
                text = "approval -> " + (FirstString(payload, "policy", "name", "value") is { Length: > 0 } av
                    ? av : "changed");
                break;

            case "session/end-seed":
                kind = "context";
                text = "session end";
                break;

            default:
                return; // Unknown / non-timeline events do not produce trajectory steps.
        }

        // Bound per-step memory: the tooltip renders at most 240 chars, so storing more across up
        // to MaxTrajectorySteps steps is pure waste (and unbounded assistant text would balloon it).
        _trajectory.Add(new TrajectoryStep(
            Index: _trajectory.Count,
            Kind: kind,
            Seq: seq,
            Time: time,
            Text: Truncate(text, MaxStepText),
            TurnIndex: _currentTurnIndex > 0 ? _currentTurnIndex : null,
            RequestIndex: _currentRequestIndex > 0 ? _currentRequestIndex : null,
            StartedAt: time > 0 ? time : null,
            DurationMs: ReadLongOrNull(payload, "durationMs", "duration", "elapsedMs", "elapsed"),
            IsError: ReadBoolOrFalse(payload, "isError", "error") || IsErrorText(text),
            CallId: ReadStringOrNull(payload, "callId", "call_id", "id"),
            Payload: ClonePayload(payload, type),
            InputTokens: ReadInputTokens(payload),
            CacheReadTokens: ReadCacheReadTokens(payload),
            CacheWriteTokens: ReadCacheWriteTokens(payload),
            OutputTokens: ReadOutputTokens(payload),
            ThinkTokens: ReadThinkTokens(payload),
            TtftMs: ReadTtftMs(payload)));
        TrajectoryVersion++;
    }

    /// <summary>True when a user/message originated from the real user (native: source.kind==='user').
    /// Host-injected content (history replay, context injection) is classified as CONTEXT.</summary>
    private static bool IsUserOrigin(JsonElement payload)
    {
        // Absent source ⇒ treat as genuine user input (matches the common wire shape).
        if (!payload.TryGetProperty("source", out var source)) return true;
        if (source.ValueKind == JsonValueKind.String)
        {
            var s = source.GetString();
            return string.IsNullOrEmpty(s) || s == "user";
        }
        if (source.ValueKind == JsonValueKind.Object
            && source.TryGetProperty("kind", out var k)
            && k.ValueKind == JsonValueKind.String)
        {
            var v = k.GetString();
            return string.IsNullOrEmpty(v) || v == "user";
        }
        return true;
    }

    private static long? ReadLongOrNull(JsonElement payload, params string[] names)
    {
        foreach (var name in names)
        {
            if (!payload.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
        }
        return null;
    }

    private static bool ReadBoolOrFalse(JsonElement payload, params string[] names)
    {
        foreach (var name in names)
        {
            if (!payload.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.True) return true;
        }
        return false;
    }

    private static string? ReadStringOrNull(JsonElement payload, params string[] names)
    {
        foreach (var name in names)
        {
            if (!payload.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }

    // ── Token metrics (native parity: trajectory-record.ts input/cacheRead/cacheWrite/output/think) ──
    // The native product reads these from the assistant message payload. Hosts differ in whether
    // they nest the counts under a `usage` object or place them at the top level, so each lookup
    // tries the nested path first and then the flat one. All are O(1) property reads and cost
    // nothing when the host doesn't provide them (every field stays null).

    private static long? ReadToken(JsonElement payload, string nativeName, params string[] extraNames)
    {
        // Build the full candidate list once: the native name first, then any caller-supplied
        // aliases. The same list is used for every location we probe.
        var names = new List<string>(extraNames.Length + 1) { nativeName };
        names.AddRange(extraNames);

        // 1) Nested: payload.usage.<name>
        if (payload.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (TryReadLong(usage, out var v, names.ToArray())) return v;
        }
        // 2) Nested: payload.assistantMetrics.<name> — the native product keeps outputTokens and
        //    the TTFT timestamps here, so token counts can legitimately live in this object too.
        if (payload.TryGetProperty("assistantMetrics", out var metrics)
            && metrics.ValueKind == JsonValueKind.Object)
        {
            if (TryReadLong(metrics, out var m, names.ToArray())) return m;
        }
        // 3) Flat: payload.<name>
        if (TryReadLong(payload, out var flat, names.ToArray())) return flat;
        return null;
    }

    private static bool TryReadLong(JsonElement obj, out long value, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out value)) return true;
        }
        value = 0;
        return false;
    }

    private static long? ReadInputTokens(JsonElement payload)
        => ReadToken(payload, "input", "inputTokens", "input_tokens", "promptTokens", "prompt_tokens");

    private static long? ReadCacheReadTokens(JsonElement payload)
        => ReadToken(payload, "cacheRead", "cacheReadTokens", "cache_read_input_tokens", "cache_read_tokens");

    private static long? ReadCacheWriteTokens(JsonElement payload)
        => ReadToken(payload, "cacheWrite", "cacheWriteTokens", "cache_creation_input_tokens", "cache_write_tokens");

    private static long? ReadOutputTokens(JsonElement payload)
        => ReadToken(payload, "output", "outputTokens", "completionTokens", "completion_tokens");

    // NOTE: assistantMetrics is now probed by ReadToken itself, so outputTokens living there
    // (as in the native AssistantMetricDetail) is picked up without a special case.

    private static long? ReadThinkTokens(JsonElement payload)
        => ReadToken(payload, "think", "thinkTokens", "think_tokens", "reasoningTokens", "reasoning_tokens");

    /// <summary>
    /// Time to first token, derived the way the native product derives it:
    /// <c>firstTokenTime − stepStartTime</c>. Only computed when both are present, so a payload
    /// without timing detail simply yields null (no fabricated numbers).
    /// </summary>
    private static long? ReadTtftMs(JsonElement payload)
    {
        if (!payload.TryGetProperty("assistantMetrics", out var metrics)
            || metrics.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (!TryReadLong(metrics, out var start, "stepStartTime")) return null;
        if (!TryReadLong(metrics, out var first, "firstTokenTime")) return null;
        long delta = first - start;
        return delta > 0 ? delta : null;
    }

    /// <summary>Last-resort error detection when the payload has no explicit error flag but the
    /// summary text already carries one (e.g. tool results whose output embeds the failure).</summary>
    private static bool IsErrorText(string text)
        => text.StartsWith("error", StringComparison.OrdinalIgnoreCase)
           || text.Contains("Exception", StringComparison.OrdinalIgnoreCase);

    /// <summary>Return the first non-empty value among the given property names, else "".</summary>
    private static string FirstString(JsonElement payload, params string[] names)
    {
        foreach (var name in names)
        {
            if (!payload.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s!;
            }
            else if (v.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                var raw = v.ToString();
                if (!string.IsNullOrWhiteSpace(raw)) return raw;
            }
        }
        return "";
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    /// <summary>Summarize a todo/write payload ({ todos: [{content, status}] } full snapshot).</summary>
    private static string DescribeTodos(JsonElement payload)
    {
        if (!payload.TryGetProperty("todos", out var todos) || todos.ValueKind != JsonValueKind.Array)
        {
            return "todo update";
        }
        var parts = new List<string>();
        foreach (var t in todos.EnumerateArray())
        {
            if (t.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            {
                var s = c.GetString();
                if (!string.IsNullOrWhiteSpace(s)) parts.Add(s!);
            }
        }
        return parts.Count == 0 ? "todo update" : "todo: " + string.Join(", ", parts);
    }

    /// <summary>
    /// C14 (P2): render a compaction checkpoint row from compaction/summary — shows the
    /// provider/model that wrote the summary and how much context was shadowed.
    /// </summary>
    private bool HandleCompaction(JsonElement eventRoot)
    {
        string provider = eventRoot.TryGetProperty("provider", out var p) ? p.GetString() ?? "" : "";
        string model = eventRoot.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
        string tokens = eventRoot.TryGetProperty("shadowedTokenCount", out var t) ? t.GetInt64().ToString() : "";
        var label = tokens.Length > 0
            ? Localization.Format("Fold.Compaction", tokens)
            : Localization.Get("Fold.CompactionNoTokens");
        if (provider.Length > 0 || model.Length > 0)
        {
            label += $" · {provider}/{model}".TrimEnd('/');
        }
        Rows.Add(new Row("compacted", label));
        return true;
    }

    /// <summary>
    /// C15 (P2): render a retry status row from llm/retry — a terminal error will be retried
    /// (normal mode) or retried unconditionally (always mode). Shows attempt count + delay.
    /// </summary>
    private bool HandleRetry(JsonElement eventRoot)
    {
        string retry = eventRoot.TryGetProperty("retry", out var r) ? r.GetInt64().ToString() : "";
        string max = eventRoot.TryGetProperty("maxRetries", out var mx) ? mx.GetInt64().ToString() : "";
        string delay = eventRoot.TryGetProperty("delayMs", out var d) ? d.GetInt64().ToString() : "";
        string mode = eventRoot.TryGetProperty("mode", out var mo) && mo.ValueKind == JsonValueKind.String
            ? mo.GetString() ?? ""
            : "";
        string label;
        if (mode == "normal" && retry.Length > 0 && max.Length > 0)
        {
            label = delay.Length > 0
                ? Localization.Format("Fold.RetryProgress", retry, max, delay)
                : Localization.Format("Fold.RetryProgressNoDelay", retry, max);
        }
        else if (mode == "always")
        {
            label = Localization.Get("Fold.RetryAlways");
        }
        else
        {
            label = Localization.Get("Fold.Retry");
        }
        Rows.Add(new Row("retry", label));
        return true;
    }

    /// <summary>Capture the whole-list todo snapshot (latest write wins).</summary>
    /// <summary>
    /// DIAGNOSTIC (2026-08-30): counts how many todo/write events this session has actually
    /// received from the host, and remembers the key set seen on the first one.
    /// <para>
    /// The Todo tab appeared permanently empty for every session, while the trajectory (same
    /// event stream) clearly works. This counter answers the decisive question without a
    /// debugger: is the host simply not sending todo/write (→ expected empty, nothing to fix),
    /// or is it sending them and we are dropping the data (→ a real parsing/notification bug)?
    /// </para>
    /// Logged at most twice per process: once for the first event (with its raw JSON shape) and
    /// once at the 100th, so a chatty session cannot spam the log.
    /// </summary>
    public int TodoWriteCount { get; private set; }

    private bool HandleTodoWrite(JsonElement eventRoot)
    {
        TodoWriteCount++;

        // Log the first event's raw shape: if the host uses a different field name than "todos",
        // this tells us immediately instead of guessing.
        if (TodoWriteCount == 1 || TodoWriteCount == 100)
        {
            try
            {
                string shape;
                if (eventRoot.ValueKind == JsonValueKind.Object)
                {
                    var names = new List<string>();
                    foreach (var p in eventRoot.EnumerateObject()) names.Add(p.Name);
                    shape = "keys=[" + string.Join(",", names) + "] json=" + Truncate(eventRoot.ToString(), 400);
                }
                else
                {
                    shape = "valueKind=" + eventRoot.ValueKind;
                }
                // Debug (not Dsh.Wpf.Logging): Dsh.App is the lower layer and must not depend
                // on the WPF shell. Debug.WriteLine still surfaces in a debugger's Output window,
                // which is all this diagnostic needs.
                System.Diagnostics.Debug.WriteLine($"[DIAG] todo/write #{TodoWriteCount} received: {shape}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException
                                         or ObjectDisposedException)
            {
                // Diagnostics must never break event handling.
                System.Diagnostics.Debug.WriteLine($"[SessionFold] todo/write diag failed: {ex.Message}");
            }
        }

        if (!eventRoot.TryGetProperty("todos", out var todos) || todos.ValueKind != JsonValueKind.Array)
        {
            // The host sent todo/write but not with a "todos" array — worth surfacing once.
            if (TodoWriteCount <= 3)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[DIAG] todo/write #{TodoWriteCount} has no 'todos' array: {Truncate(eventRoot.ToString(), 400)}");
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException
                                             or ObjectDisposedException)
                {
                    // Diagnostics must never break event handling.
                    System.Diagnostics.Debug.WriteLine($"[SessionFold] todo/write diag failed: {ex.Message}");
                }
            }
            return false;
        }

        var list = new List<TodoItem>();
        foreach (var item in todos.EnumerateArray())
        {
            string content = item.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            string status = item.TryGetProperty("status", out var s) ? s.GetString() ?? "pending" : "pending";
            list.Add(new TodoItem { Content = content, Status = status });
        }

        if (TodoWriteCount <= 3)
        {
            System.Diagnostics.Debug.WriteLine($"[DIAG] todo/write #{TodoWriteCount} parsed {list.Count} todo item(s)");
        }
        Todos = list;
        return true;
    }

    private bool AppendChunk(JsonElement eventRoot)
    {
        // A2/A3 monitoring hook: measure AppendChunk wall-clock time. Each call rebuilds
        // Rows[^1].Text via _textBuffer.ToString() (O(n) copy) — the A2 concern. Timed here so
        // the UI sampler can detect when AvgAppendMicros or chunks/s cross the thresholds.
        _appendWatch.Restart();
        try
        {
            if (!eventRoot.TryGetProperty("chunk", out var chunk) ||
                !chunk.TryGetProperty("type", out var ct) ||
                !chunk.TryGetProperty("text", out var text) ||
                text.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            switch (ct.GetString())
            {
                case "text-delta":
                    EnsureAssistant();
                    _textBuffer.Append(text.GetString());
                    break;
                case "reasoning-delta":
                    EnsureAssistant();
                    _reasoningBuffer.Append(text.GetString());
                    break;
                default:
                    return false;
            }

            // P0-1 (O(n²) → O(n)): only append to the buffer here — do NOT materialize
            // Rows[^1].Text yet. Materializing on every chunk means a full StringBuilder→string
            // copy per chunk, i.e. O(m·n) = O(n²) for an n-char message split into m chunks,
            // all of it on the UI thread. Instead we just remember which row is pending and
            // materialize it once, lazily, when something actually reads Rows (see
            // MaterializePendingRow, called before rendering).
            _pendingRowIndex = Rows.Count - 1;
            _chunkCount++;
            return true;
        }
        finally
        {
            _appendWatch.Stop();
            _appendMicros += (long)_appendWatch.Elapsed.TotalMicroseconds;
            if (Rows.Count > _peakRows) _peakRows = Rows.Count;
        }
    }

    /// <summary>Finalize from the <c>assistant/message</c> content block array (authoritative).
    /// An empty content array is NOT authoritative over streamed chunk text (a provider may
    /// emit a finalizing message with no blocks); only non-empty content replaces the buffer.</summary>
    private bool FinalizeFromMessage(JsonElement eventRoot, long eventTime)
    {
        // H5: the assistant message's id lets a UI attach feedback (messageFeedback.put).
        string? messageId = eventRoot.TryGetProperty("message", out var msg) &&
                            msg.TryGetProperty("id", out var mid) && mid.ValueKind == JsonValueKind.String
            ? mid.GetString()
            : null;

        if (eventRoot.TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Array &&
            content.GetArrayLength() > 0)
        {
            // Track whether an assistant row is already in-flight from streamed chunks. We
            // must NOT Finalize() (which clears _activeRole) before EnsureAssistant(), or the
            // finalizing assistant/message would materialize a duplicate row instead of
            // replacing the streamed one (this is the "assistant/message should be
            // authoritative over the chunked buffer" contract).
            bool inFlightAssistant = _activeRole == "assistant";

            // E10 (2026-08-24): host's history-tail assistant/message can carry a truncated
            // content[] (e.g. only the first few text blocks after a re-fetch), which would
            // otherwise Clear() our accumulated chunk buffer and overwrite Rows[^1] with a
            // shorter Text. Snapshot the streamed buffer before clearing and, if the extracted
            // text is shorter than what we already had, restore the streamed buffer so the
            // chunk history wins. Length-only comparison is intentional: when the host ships a
            // legitimately-revised final (e.g. streaming order fix), its content[] should be
            // >= the chunk buffer; only one-sided truncations are rejected.
            string? preClearedText = inFlightAssistant ? _textBuffer.ToString() : null;
            string? preClearedReason = inFlightAssistant && _reasoningBuffer.Length > 0
                ? _reasoningBuffer.ToString()
                : null;

            // P0-1: commit any in-flight streamed text before the buffers are cleared and before
            // new rows are appended, so the pending assistant row keeps everything accumulated.
            MaterializePendingRow();

            _textBuffer.Clear();
            _reasoningBuffer.Clear();
            bool addedRow = false;
            bool anyTextOrReasoning = false;
            string? reasoning = null;
            foreach (var block in content.EnumerateArray())
            {
                string blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() ?? "" : "";
                if (blockType == "tool-call")
                {
                    // Recursive tool call: build a tree row from the block (with subCalls).
                    var node = ToolCallNode.FromBlock(block);
                    Rows.Add(new Row("tool", $"🔧 {node.Name}", Tool: node));
                    addedRow = true;
                    continue;
                }
            }
            // Flatten text/reasoning blocks from the content array (host contract).
            var text = ExtractTextFromContent(content, out reasoning);
            _textBuffer.Append(text);
            if (reasoning != null) _reasoningBuffer.Append(reasoning);

            // Restore streamed buffer if the message content is shorter than what chunks gave us.
            if (preClearedText is { Length: > 0 } && _textBuffer.Length < preClearedText.Length)
            {
                _textBuffer.Clear();
                _textBuffer.Append(preClearedText);
            }
            if (preClearedReason is { Length: > 0 } && _reasoningBuffer.Length < preClearedReason.Length)
            {
                _reasoningBuffer.Clear();
                _reasoningBuffer.Append(preClearedReason);
            }

            anyTextOrReasoning = _textBuffer.Length > 0 || _reasoningBuffer.Length > 0;
            if (anyTextOrReasoning)
            {
                // If no assistant row is in-flight (history path where assistant/message is the
                // first surface-bearing event), materialize one. Otherwise reuse the in-flight row.
                if (!inFlightAssistant)
                {
                    EnsureAssistant();
                }
                var last = Rows.Count > 0 ? Rows[^1] : null;
                if (last is { Role: "assistant" })
                {
                    Rows[^1] = last with
                    {
                        Text = _textBuffer.ToString(),
                        Reasoning = _reasoningBuffer.Length == 0 ? null : _reasoningBuffer.ToString(),
                        MessageId = messageId ?? last.MessageId,
                        Time = eventTime > 0 ? eventTime : last.Time,
                    };
                    // P0-1: the row now carries the authoritative text; nothing is pending.
                    _pendingRowIndex = -1;
                }
            }
            // Legacy safety: if no content produced a row and none was active, avoid
            // touching Rows[^1] (could be a user row from the previous turn).
            _ = addedRow;
        }

        return Finalize();
    }

    private bool AddToolCall(JsonElement eventRoot, JsonElement? view = null)
    {
        Finalize();
        if (!eventRoot.TryGetProperty("callId", out var callId) ||
            !eventRoot.TryGetProperty("name", out var name))
        {
            return false;
        }

        string? args = eventRoot.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString()
            : null;

        // P2-5: cache the tool view so a later tool/result can derive produced files from
        // the mutation tool's own locations (by render intent, not tool name).
        // The view comes from the transport (mux frame / history entry), which is where the
        // host actually puts it; the legacy data.view probe stays as a fallback for shapes
        // that inline it.
        var effective = view is { ValueKind: JsonValueKind.Object } v ? v
            : eventRoot.TryGetProperty("view", out var inline) && inline.ValueKind == JsonValueKind.Object
                ? inline
                : (JsonElement?)null;
        if (effective is not null)
        {
            _callViews[callId.GetString() ?? ""] = effective.Value;
        }

        Rows.Add(new Row(
            "tool",
            "",
            Tool: new ToolCallNode(callId.GetString() ?? "", name.GetString() ?? "", args, "running", null)));
        return true;
    }

    private bool SettleToolResult(JsonElement eventRoot, long eventSeq, JsonElement? view = null)
    {
        // tool/result → { message: { content: [ { type:'tool-result', toolCallId, content, isError? } ] } }
        if (!eventRoot.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        string? toolCallId = null;
        string? output = null;
        bool isError = false;

        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var bt) && bt.GetString() == "tool-result")
            {
                toolCallId = block.TryGetProperty("toolCallId", out var ci) ? ci.GetString() : null;
                isError = block.TryGetProperty("isError", out var ie) && ie.GetBoolean();
                if (block.TryGetProperty("content", out var inner) && inner.ValueKind == JsonValueKind.Array)
                {
                    output = ExtractTextBlocks(inner);
                }
                break;
            }
        }

        if (toolCallId is null) return false;

        // P2-5: derive produced files by render intent (a diff card, or a generic card whose
        // kind is "edit"); failed calls produce nothing. The result's own view (presentResult)
        // is authoritative when present — it reflects the COMPLETED mutation — falling back to
        // the view cached from the tool/call (presentCall), then to a legacy inline data.view.
        JsonElement? effectiveView = view is { ValueKind: JsonValueKind.Object } ? view : null;
        if (effectiveView is null && eventRoot.TryGetProperty("view", out var inlineView)
            && inlineView.ValueKind == JsonValueKind.Object)
        {
            effectiveView = inlineView;
        }
        if (!isError)
        {
            if (effectiveView is not null)
            {
                AppendProducedPaths(effectiveView.Value, eventSeq);
            }
            else if (_callViews.TryGetValue(toolCallId, out var cached))
            {
                AppendProducedPaths(cached, eventSeq);
            }
        }

        // Update the matching tool row by callId.
        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            if (Rows[i].Tool is { } node && node.CallId == toolCallId)
            {
                Rows[i] = Rows[i] with
                {
                    Tool = node.WithResult(isError ? "error" : "done", output),
                    Text = $"🔧 {node.Name}",
                };
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Collect produced-file paths from a cached tool view (P2-5): the view must be a diff
    /// card, or a generic card whose <c>kind</c> is <c>edit</c>; then each <c>locations[].path</c>
    /// is appended in first-seen order, deduped within the active turn.
    /// </summary>
    private void AppendProducedPaths(JsonElement view, long seq)
    {
        foreach (var path in ExtractProducedPaths(view))
        {
            if (_deliverables.Any(d => d.Path == path)) continue;
            _deliverables.Add(new DeliverableItem(seq, path));
            DeliverablesVersion++;   // publish to the UI (see DeliverablesVersion doc)
        }
    }

    /// <summary>
    /// Paths a tool view reports as produced, by render intent (P2-5): a diff card, or a
    /// generic card whose <c>kind</c> is <c>edit</c>. Any other card produces nothing.
    /// </summary>
    public static string[] ExtractProducedPaths(JsonElement view)
    {
        string card = view.TryGetProperty("card", out var c) ? c.GetString() ?? "" : "";
        bool isMutation = card == "diff"
            || (card == "generic"
                && view.TryGetProperty("kind", out var k) && k.GetString() == "edit");
        if (!isMutation) return Array.Empty<string>();

        if (!view.TryGetProperty("locations", out var locations) || locations.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var paths = new List<string>();
        for (int i = 0; i < locations.GetArrayLength(); i++)
        {
            var location = locations[i];
            if (!location.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            var path = p.GetString() ?? "";
            if (path.Length == 0) continue;
            paths.Add(path);
        }
        return paths.ToArray();
    }

    /// <summary>Open a turn: insert a turn-separator row so the surface groups by turn.</summary>
    private bool StartTurn(JsonElement eventRoot)
    {
        // P2-fix: the host wire has evolved across releases — older payloads used
        // { "turn": <int> }, newer ones use { "turnId": <int> } / { "id": <int> }.
        // Whatever the field name, the separator row is more important than a missing number,
        // so fall back to "next turn" derived from existing rows when the field is absent.
        int turn = 0;
        if (eventRoot.TryGetProperty("turn", out var t)) turn = t.GetInt32();
        else if (eventRoot.TryGetProperty("turnId", out var tid)) turn = tid.GetInt32();
        else if (eventRoot.TryGetProperty("id", out var id)) turn = id.GetInt32();
        if (turn <= 0)
        {
            // Fallback: take the max turn number seen in existing rows and add 1.
            int maxSeen = 0;
            foreach (var r in Rows)
            {
                if (r.Role != "turn") continue;
                var m = System.Text.RegularExpressions.Regex.Match(r.Text, @"\d+");
                if (m.Success && int.TryParse(m.Value, out var n) && n > maxSeen) maxSeen = n;
            }
            turn = maxSeen + 1;
        }
        // P2-5: a new turn resets the produced-file accumulator and view cache.
        _deliverables.Clear();
        _callViews.Clear();
        DeliverablesVersion++;   // publish the cleared list (NOT a self-assignment)
        Rows.Add(new Row("turn", Localization.Format("Fold.Turn", turn)));
        return true;
    }

    /// <summary>Close a turn: finalize any active assistant and append the end reason.</summary>
    private bool EndTurn(JsonElement eventRoot)
    {
        Finalize();
        // C16 (P2): terminal failure — reason.kind === "error" carries a structured LlmFailure.
        // Render it as a visible error row (code + message), with AUTH specially worded so the
        // UI never echoes a potentially credential-bearing message. Non-error reasons produce
        // the usual turn marker.
        if (eventRoot.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.Object)
        {
            if (reasonEl.TryGetProperty("kind", out var rk) && rk.GetString() == "error")
            {
                var error = reasonEl.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                    ? err
                    : default;
                string code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var ec)
                    ? ec.GetString() ?? ""
                    : "";
                string message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var em)
                    ? em.GetString() ?? ""
                    : "";
                var text = code == "AUTH"
                    ? Localization.Get("Fold.TerminalAuth")
                    : (message.Length == 0
                        ? Localization.Get("Fold.TerminalFailed")
                        : Localization.Format("Fold.TerminalFailedMessage", message));
                Rows.Add(new Row("error", text));
                return true;
            }
        }
        // Reason may be a string (legacy) or a structured object like { kind: "completed" };
        // never call GetString on an object — it throws InvalidOperationException.
        string reason = ResolveReason(eventRoot);
        Rows.Add(new Row("turn", reason.Length == 0 ? "✓" : $"✓ {reason}"));
        return true;
    }

    private static string ResolveReason(JsonElement eventRoot)
    {
        if (!eventRoot.TryGetProperty("reason", out var r)) return "";
        if (r.ValueKind == JsonValueKind.String) return r.GetString() ?? "";
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String)
        {
            return k.GetString() ?? "";
        }
        return "";
    }

    /// <summary>单个 tool result 的文本内容总长上限。超过则截断并追加标记，避免超大输出
    /// 进 Markdig 同步解析导致 UI 卡死（2026-08-24 用户报告特定会话加载卡死）。</summary>
    private const int MaxToolOutputBytes = 1 * 1024 * 1024;

    private static string? ExtractTextBlocks(JsonElement blocks)
    {
        var sb = new StringBuilder();
        foreach (var block in blocks.EnumerateArray())
        {
            if (sb.Length > MaxToolOutputBytes) break;
            if (block.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                string s = text.GetString() ?? "";
                int room = MaxToolOutputBytes - sb.Length;
                sb.AppendLine(room >= s.Length ? s : s[..room]);
            }
        }
        if (sb.Length == 0) return null;
        if (sb.Length >= MaxToolOutputBytes)
        {
            // 超限标记：保留首部诊断信息，注明已截断。
            sb.AppendLine("… (truncated: tool output exceeded limit)");
        }
        return sb.ToString().TrimEnd();
    }

    private bool EnsureAssistant()
    {
        if (_activeRole == "assistant") return false;
        // P0-1: commit any in-flight streamed text before starting a new assistant row and
        // clearing the buffers — otherwise the previous row's accumulated text would be lost.
        MaterializePendingRow();
        _activeRole = "assistant";
        _textBuffer.Clear();
        _reasoningBuffer.Clear();
        Rows.Add(new Row("assistant", ""));
        return true;
    }

    private bool Finalize()
    {
        if (_activeRole is null) return false;
        // P0-1: commit in-flight streamed text before the buffers are cleared.
        MaterializePendingRow();
        _activeRole = null;
        _textBuffer.Clear();
        _reasoningBuffer.Clear();
        return false;
    }

    private bool FinalizeThenAddUser(JsonElement eventRoot, long eventTime)
    {
        Finalize();
        // Host wire: user/message.data.content is a ContentBlock[] (not a bare string).
        // `eventRoot` here is the event's `data` object, so dig into `.content`.
        var text = ExtractTextFromContent(GetContent(eventRoot), out var _);
        // H5: the user message's id (messageFeedback.put) — user/message carries data.id
        // directly on the Message envelope (see deepseek-harness api/sessions.ts).
        string? messageId = eventRoot.TryGetProperty("id", out var mid) && mid.ValueKind == JsonValueKind.String
            ? mid.GetString()
            : null;
        // C13 (P2): context injection disclosure. A user/message whose source.kind is not
        // "user" (e.g. plugin / agent-instructions / skill-invocation / session-reference) is
        // injected context, not an authored prompt — render it as a context disclosure row with
        // a producer label instead of a plain user bubble.
        var role = "user";
        // C13 (P2): a source that is an object with a non-"user" kind denotes injected
        // context. A string/legacy source (e.g. "prompt") is treated as an authored prompt.
        if (eventRoot.TryGetProperty("source", out var source) &&
            source.ValueKind == JsonValueKind.Object &&
            source.TryGetProperty("kind", out var kind) &&
            kind.ValueKind == JsonValueKind.String &&
            kind.GetString() != "user")
        {
            role = "context";
            text = BuildContextDisclosure(source, kind.GetString() ?? "inject", text);
        }
        // Merge into a preceding optimistic client user row instead of appending a duplicate
        // (see TryMergeOptimisticUser). Only authored prompts (role == "user") qualify.
        if (role == "user" && TryMergeOptimisticUser(text, messageId, eventTime > 0 ? eventTime : null))
        {
            return true;
        }
        Rows.Add(new Row(role, text, MessageId: messageId, Time: eventTime > 0 ? eventTime : null));
        return true;
    }

    /// <summary>
    /// Host wire contract (see deepseek-harness packages/host/apiproxy/src/api/sessions.ts):
    /// a message's `content` is a ContentBlock[] where each block is
    /// `{type:'text'|'reasoning'|'tool-call'|'tool-result'|'image', text?, ...}`.
    /// User/assistant bubble text is the concatenation of `text` blocks only (reasoning is
    /// rendered separately). This helper flattens that array; `out reasoning` returns the
    /// concatenated reasoning blocks (used by assistant rows).
    /// </summary>
    /// <summary>
    /// Resolve the ContentBlock[] element from a `user/message` / `assistant/message` payload.
    /// `payload` is the event's `data` object ({content, ...}), so the actual blocks live under
    /// `content`. Falls back to the payload itself when `content` is absent (legacy / test shapes).
    /// </summary>
    private static JsonElement GetContent(JsonElement payload)
    {
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("content", out var content)
            ? content
            : payload;
    }

    private static string ExtractTextFromContent(JsonElement contentRoot, out string? reasoning)
    {
        var text = new StringBuilder();
        var reasoningBuf = new StringBuilder();
        if (contentRoot.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in contentRoot.EnumerateArray())
            {
                string blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() ?? "" : "";
                if (!block.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String) continue;
                if (blockType == "reasoning")
                {
                    reasoningBuf.Append(t.GetString());
                }
                else if (blockType == "text")
                {
                    text.Append(t.GetString());
                }
            }
        }
        // Fallback: some frames still carry a bare string content (legacy / provisional).
        else if (contentRoot.ValueKind == JsonValueKind.String)
        {
            text.Append(contentRoot.GetString());
        }
        reasoning = reasoningBuf.Length == 0 ? null : reasoningBuf.ToString();
        return text.ToString();
    }

    /// <summary>
    /// De-duplicate a client-side optimistic user row against a host-replayed user/message event.
    /// When the client inserts a user bubble eagerly on send (MessageId == null) and the host later
    /// replays the authoritative user/message frame (carrying the real message id), we must not
    /// append a second bubble — instead we promote the existing optimistic row in place so the
    /// chat surface shows exactly one authored-prompt bubble (mirrors web behaviour).
    /// </summary>
    private bool TryMergeOptimisticUser(string text, string? messageId, long? time)
    {
        if (Rows.Count == 0) return false;

        // The merge window: how far back from the tail to look for an optimistic user row.
        // Empirically the host may interleave turn/tool/assistant events BEFORE replaying the
        // authoritative user/message, so the optimistic row is no longer last. Walking all
        // the way back would risk merging into an unrelated old prompt; a small bounded
        // window covers every realistic replay order while keeping the operation O(1).
        const int MaxMergeLookback = 8;

        for (int offset = 0; offset < MaxMergeLookback && offset < Rows.Count; offset++)
        {
            int idx = Rows.Count - 1 - offset;
            var row = Rows[idx];
            if (row.Role != "user") continue;        // skip turn/tool/assistant rows
            if (row.MessageId is not null)
            {
                // First authoritative user row we hit walking back means a previous prompt's
                // message already settled; an earlier optimistic row would be from a different
                // turn and must not be merged with this replay.
                return false;
            }
            if (!string.Equals(row.Text, text, StringComparison.Ordinal)) return false;
            Rows[idx] = row with { MessageId = messageId, Time = time };
            return true;
        }
        return false;
    }

    /// <summary>
    /// Build a disclosure label for injected context (C13, P2): prefixes the producer name
    /// (plugin / agent-instructions / skill-invocation / session-reference / else the kind)
    /// so the row reads as "context · <producer>".
    /// </summary>
    private static string BuildContextDisclosure(JsonElement source, string kind, string content)
    {
        string producer = kind;
        if (kind == "agent-instructions" &&
            source.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array &&
            changes.GetArrayLength() > 0 &&
            changes[0].TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
        {
            producer = $"agent-instructions {p.GetString()}";
        }
        else if (kind == "plugin" &&
                 source.TryGetProperty("plugin", out var plug) && plug.ValueKind == JsonValueKind.String)
        {
            producer = $"plugin {plug.GetString()}";
        }
        return content.Length == 0
            ? Localization.Format("Fold.ContextInjected", producer)
            : $"{content}\n{Localization.Format("Fold.ContextInjectedSuffix", producer)}";
    }

    // DiffLine — extracted to SessionFold.ToolCallNode.cs
}
