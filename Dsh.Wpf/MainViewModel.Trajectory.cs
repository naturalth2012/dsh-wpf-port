using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.App;

namespace Dsh.Wpf;

/// <summary>
/// Trajectory ledger (native parity: packages/client/ui-trajectory).
///
/// The native product shows a timeline on top plus a virtualized, turn/request-grouped ledger
/// table below, with a details pane for the selected step. This partial supplies the view model
/// for that ledger: a FLAT row list (so ListBox virtualization stays effective), search, folding,
/// and a lazily-built details pane.
///
/// Performance notes (this view is rebuilt while streaming, so the hot paths matter):
///  • Rows are rebuilt ONLY when the trajectory version, the search text, or a fold toggle
///    changed — the 250 ms render flush does not rebuild rows when nothing changed.
///  • The details pane's Payload text (JSON re-serialization — the most expensive part) is built
///    lazily on first read and cached, so it costs nothing until the user opens that tab.
///  • Selection sync between the table and the timeline is guarded against re-entrancy.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Flat, virtualized display rows for the ledger (headers + steps).</summary>
    public ObservableCollectionEx<TrajectoryRow> TrajectoryRows { get; } = new();

    /// <summary>Case-insensitive ledger filter; empty shows everything.</summary>
    [ObservableProperty]
    private string trajectorySearchText = "";

    /// <summary>When true only Turn headers are shown (native "Collapse turns").</summary>
    [ObservableProperty]
    private bool trajectoryCollapseTurns;

    /// <summary>When true tool steps are hidden (native "Collapse calls").</summary>
    [ObservableProperty]
    private bool trajectoryCollapseCalls;

    /// <summary>Currently selected ledger row (a step row, or null).</summary>
    [ObservableProperty]
    private TrajectoryRow? selectedTrajectoryRow;

    /// <summary>Focused step index, shared with the timeline so both views track each other.</summary>
    [ObservableProperty]
    private int trajectoryFocusIndex = -1;

    /// <summary>True while the details pane is expanded.</summary>
    [ObservableProperty]
    private bool trajectoryDetailExpanded;

    // ── Details pane (plain properties: the JSON-heavy tabs are built lazily, so they are not
    //    auto-properties — building them eagerly would tax the streaming hot path for tabs the
    //    user may never open) ──
    private string _detailSummary = "";
    private string _detailTiming = "";
    private string? _detailPayload;      // null = not built yet
    private string? _detailResult;       // null = not built yet
    private string? _detailArguments;    // null = not built yet

    public string TrajectoryDetailSummary => _detailSummary;
    public string TrajectoryDetailTiming => _detailTiming;

    /// <summary>
    /// Pretty-printed payload JSON. Built on FIRST read only — re-serializing a payload is the
    /// single most expensive thing this pane can do, and most steps are never opened, so paying
    /// for it up front would be pure waste on the streaming hot path.
    /// </summary>
    public string TrajectoryDetailPayload => _detailPayload ??= BuildPayloadDetail();

    /// <summary>Tool output (native "Result" tab): the output/result field of a tool result step,
    /// or a note that this step kind carries no result. Built lazily like the payload.</summary>
    public string TrajectoryDetailResult => _detailResult ??= BuildFieldDetail("output", "result", "content");

    /// <summary>Tool call arguments (sits where the native ledger's "Schema" tab sits; named
    /// honestly because the WPF wire payload carries arguments, not tool input schemas).
    /// Built lazily like the payload.</summary>
    public string TrajectoryDetailArguments => _detailArguments ??= BuildFieldDetail("arguments", "args", "input");

    /// <summary>True when the selected step actually has a payload to show.</summary>
    public bool HasTrajectoryPayload => SelectedTrajectoryRow?.Step?.Payload is not null;

    // ── Row rebuild bookkeeping ───────────────────────────────────────────────────────────────
    private int _rowsBuiltVersion = -1;
    private string? _rowsBuiltSearch;
    private bool _rowsBuiltCollapseTurns;
    private bool _rowsBuiltCollapseCalls;

    /// <summary>Reflects whether the current row set is hiding anything (drives the hint text).</summary>
    [ObservableProperty]
    private string trajectoryFilterHint = "";

    partial void OnTrajectoryChanged(IReadOnlyList<SessionFold.TrajectoryStep> value)
        => RebuildTrajectoryRows();

    partial void OnTrajectorySearchTextChanged(string value) => RebuildTrajectoryRows();

    partial void OnTrajectoryCollapseTurnsChanged(bool value) => RebuildTrajectoryRows();

    partial void OnTrajectoryCollapseCallsChanged(bool value) => RebuildTrajectoryRows();

    /// <summary>
    /// Rebuild the flat row list, but ONLY when an input actually changed. Called from the
    /// trajectory/search/fold setters.
    /// </summary>
    private void RebuildTrajectoryRows()
    {
        int version = _fold?.TrajectoryVersion ?? -1;
        string search = TrajectorySearchText ?? "";
        if (_rowsBuiltVersion == version
            && string.Equals(_rowsBuiltSearch, search, StringComparison.Ordinal)
            && _rowsBuiltCollapseTurns == TrajectoryCollapseTurns
            && _rowsBuiltCollapseCalls == TrajectoryCollapseCalls)
        {
            return;   // nothing changed — skip the O(n) rebuild entirely.
        }

        _rowsBuiltVersion = version;
        _rowsBuiltSearch = search;
        _rowsBuiltCollapseTurns = TrajectoryCollapseTurns;
        _rowsBuiltCollapseCalls = TrajectoryCollapseCalls;

        // Snapshot the fold's live list BEFORE handing it to Build: the host streams new steps
        // continuously on the UI thread (via AppendEvent → UiThrottle → SyncFoldToUi) and any
        // mutation of _trajectory while Build is enumerating throws
        // "Collection was modified; enumeration may not execute." (real exception seen on
        // 2026-08-31 connect: the line 380 catch SyncFoldToUi → Trajectory = ToArray() →
        // RebuildTrajectoryRows → TrajectoryLedger.Build was reading _fold.Trajectory while
        // streaming events were appending to it). Build only reads — it never sees live updates
        // once it starts — and the snapshot is cheap (one Array copy of the current view).
        var snapshot = _fold?.Trajectory.ToArray() ?? Array.Empty<SessionFold.TrajectoryStep>();
        var rows = TrajectoryLedger.Build(
            snapshot,
            TrajectoryCollapseTurns,
            TrajectoryCollapseCalls,
            search);

        // Preserve the selection across the rebuild: the row objects are new, so re-resolve the
        // previously focused step instead of silently dropping the user's selection.
        int keepIndex = SelectedTrajectoryRow?.Step?.Index ?? TrajectoryFocusIndex;
        TrajectoryRow? restored = null;
        if (keepIndex >= 0)
        {
            restored = rows.FirstOrDefault(r => r.Step is { } s && s.Index == keepIndex);
        }

        TrajectoryRows.ReplaceAll(rows);   // ONE Reset notification, not one per row.

        if (restored is not null)
        {
            SetSelectedRowWithoutSync(restored);
        }
        else if (keepIndex >= 0 && rows.Count == 0)
        {
            // The step we were showing is filtered out; clear the details pane.
            SetSelectedRowWithoutSync(null);
        }

        TrajectoryFilterHint = BuildFilterHint(rows.Count, search);
        RebuildDetail();
    }

    private void SetSelectedRowWithoutSync(TrajectoryRow? row)
    {
        _syncingTrajectorySelection = true;
        try
        {
            SelectedTrajectoryRow = row;
            TrajectoryFocusIndex = row?.Step?.Index ?? -1;
        }
        finally
        {
            _syncingTrajectorySelection = false;
        }
    }

    private string BuildFilterHint(int rowCount, string search)
    {
        int total = _fold?.Trajectory.Count ?? 0;
        bool filtering = !string.IsNullOrWhiteSpace(search);
        if (!filtering && !TrajectoryCollapseTurns && !TrajectoryCollapseCalls)
        {
            return total == 0 ? "" : $"{total} steps";
        }
        int stepRows = TrajectoryRows.Count(r => r.RowKind == TrajectoryRowKind.Step);
        return filtering ? $"{stepRows}/{total} matched" : $"{stepRows}/{total} shown";
    }

    // ── Selection sync (table ↔ timeline) ─────────────────────────────────────────────────────
    private bool _syncingTrajectorySelection;

    partial void OnSelectedTrajectoryRowChanged(TrajectoryRow? value)
    {
        if (_syncingTrajectorySelection) return;
        _syncingTrajectorySelection = true;
        try
        {
            TrajectoryFocusIndex = value?.Step?.Index ?? -1;
        }
        finally
        {
            _syncingTrajectorySelection = false;
        }
        RebuildDetail();
    }

    partial void OnTrajectoryFocusIndexChanged(int value)
    {
        if (_syncingTrajectorySelection) return;
        if (value < 0) return;
        var row = TrajectoryRows.FirstOrDefault(r => r.Step is { } s && s.Index == value);
        if (row is null) return;   // step not present (filtered out or not yet built)
        _syncingTrajectorySelection = true;
        try
        {
            SelectedTrajectoryRow = row;
        }
        finally
        {
            _syncingTrajectorySelection = false;
        }
        RebuildDetail();
    }

    // ── Details pane ──────────────────────────────────────────────────────────────────────────
    private void RebuildDetail()
    {
        var step = SelectedTrajectoryRow?.Step;
        _detailPayload = null;                 // invalidate the lazy caches
        _detailResult = null;
        _detailArguments = null;
        _detailSummary = BuildSummaryDetail(step);
        _detailTiming = BuildTimingDetail(step);
        OnPropertyChanged(nameof(TrajectoryDetailSummary));
        OnPropertyChanged(nameof(TrajectoryDetailPayload));
        OnPropertyChanged(nameof(TrajectoryDetailResult));
        OnPropertyChanged(nameof(TrajectoryDetailArguments));
        OnPropertyChanged(nameof(TrajectoryDetailTiming));
        OnPropertyChanged(nameof(HasTrajectoryPayload));
    }

    /// <summary>
    /// Extract one named field from the selected step's payload, pretty-printed. Shared by the
    /// Result (output/result/content) and Arguments (arguments/args/input) tabs so both get the
    /// same lazy-build + graceful-fallback behaviour. A missing payload or field yields an honest
    /// "(not captured for this step)" note rather than an empty pane.
    /// </summary>
    private string BuildFieldDetail(params string[] fieldNames)
    {
        var payload = SelectedTrajectoryRow?.Step?.Payload;
        if (payload is null) return "(no payload captured for this step)";
        foreach (var name in fieldNames)
        {
            if (!payload.Value.TryGetProperty(name, out var field)) continue;
            if (field.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                try
                {
                    return JsonSerializer.Serialize(field, new JsonSerializerOptions { WriteIndented = true });
                }
                catch
                {
                    return field.ToString();
                }
            }
            if (field.ValueKind == JsonValueKind.String)
            {
                var s = field.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        return "(no " + string.Join("/", fieldNames) + " captured for this step)";
    }

    private static string BuildSummaryDetail(SessionFold.TrajectoryStep? step)
    {
        if (step is null) return "";
        var sb = new System.Text.StringBuilder();
        sb.Append(TrajectoryLedger.KindLabel(step.Kind));
        if (step.TurnIndex is { } turn) sb.Append(" · Turn ").Append(turn);
        if (step.RequestIndex is { } req) sb.Append(" · Request #").Append(req);
        if (step.IsError) sb.Append(" · ERROR");
        sb.AppendLine();
        sb.AppendLine();
        sb.Append(ExtractFullText(step));
        return sb.ToString().TrimEnd();
    }

    private static string BuildTimingDetail(SessionFold.TrajectoryStep? step)
    {
        if (step is null) return "";
        var lines = new List<string>
        {
            $"step      #{step.Index + 1}",
            $"seq       {step.Seq}",
            $"kind      {step.Kind}",
        };
        if (step.TurnIndex is { } turn) lines.Add($"turn      {turn}");
        if (step.RequestIndex is { } req) lines.Add($"request   #{req}");
        lines.Add($"time      +{FormatMs(step.Time)}");
        if (step.StartedAt is { } started) lines.Add($"started   +{FormatMs(started)}");
        if (step.DurationMs is { } dur) lines.Add($"duration  {TrajectoryLedger.FormatDuration(dur)}");
        if (step.TtftMs is { } ttft) lines.Add($"ttft      {TrajectoryLedger.FormatDuration(ttft)}");
        if (step.CallId is { Length: > 0 } call) lines.Add($"call      {call}");
        lines.Add($"error     {(step.IsError ? "yes" : "no")}");

        // Token metrics (native parity: input/cacheRead/cacheWrite/output/think). Only listed
        // when the host actually provided them, so we never show fabricated zeros.
        if (HasAnyToken(step))
        {
            lines.Add("");
            lines.Add("[tokens]");
            if (step.InputTokens is { } input) lines.Add($"in        {input:N0}");
            if (step.CacheReadTokens is { } read) lines.Add($"cache+    {read:N0}");
            if (step.CacheWriteTokens is { } write) lines.Add($"cache-    {write:N0}");
            if (step.OutputTokens is { } output) lines.Add($"out       {output:N0}");
            if (step.ThinkTokens is { } think) lines.Add($"think     {think:N0}");
            if (step.TtftMs is { } ttft2 && step.OutputTokens is { } out2 && ttft2 > 0)
            {
                lines.Add($"decode    {out2 / (ttft2 / 1000.0):0.0} tok/s");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>True when the step carries at least one token count, so the token block is only
    /// rendered when there is something real to show.</summary>
    private static bool HasAnyToken(SessionFold.TrajectoryStep step)
        => step.InputTokens.HasValue
           || step.CacheReadTokens.HasValue
           || step.CacheWriteTokens.HasValue
           || step.OutputTokens.HasValue
           || step.ThinkTokens.HasValue;

    private string BuildPayloadDetail()
    {
        var payload = SelectedTrajectoryRow?.Step?.Payload;
        if (payload is null) return "(no payload captured for this step)";
        try
        {
            return JsonSerializer.Serialize(payload.Value, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception)
        {
            // Fall back to the raw text if pretty-printing fails for any reason.
            return payload.Value.ToString();
        }
    }

    /// <summary>Best available full text for a step. The stored <c>Text</c> is truncated for the
    /// list row, so when the payload survived we pull the untruncated value out of it.</summary>
    private static string ExtractFullText(SessionFold.TrajectoryStep step)
    {
        if (step.Payload is { } payload && payload.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "text", "content", "output", "result", "summary", "arguments", "args", "input" })
            {
                if (!payload.TryGetProperty(name, out var v)) continue;
                if (v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s!;
                }
            }
        }
        return step.Text;
    }

    private static string FormatMs(long ms)
    {
        if (ms <= 0) return "0s";
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.TotalMinutes >= 1 ? $"{ts.Minutes}:{ts.Seconds:00}" : $"{ts.TotalSeconds:0.0}s";
    }

    /// <summary>Clear the ledger filter / folds back to their defaults.</summary>
    [RelayCommand]
    private void ClearTrajectoryFilter()
    {
        TrajectorySearchText = "";
        TrajectoryCollapseTurns = false;
        TrajectoryCollapseCalls = false;
    }
}
