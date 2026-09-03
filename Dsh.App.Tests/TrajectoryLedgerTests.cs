using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Dsh.App.Tests;

/// <summary>
/// Unit tests for <see cref="TrajectoryLedger"/> — the pure flattening logic behind the WPF
/// trajectory view (turn/request grouping, folding, search, duration formatting). Kept free of
/// WPF dependencies precisely so these rules can be tested without a dispatcher.
/// </summary>
public sealed class TrajectoryLedgerTests
{
    private static SessionFold.TrajectoryStep Step(
        int index,
        string kind,
        string text,
        int? turn = null,
        int? request = null,
        bool isError = false,
        string? callId = null,
        long? durationMs = null)
        => new(index, kind, index, index * 100, text, turn, request,
               StartedAt: null, DurationMs: durationMs, IsError: isError, CallId: callId);

    [Fact]
    public void Emits_turn_and_request_headers_on_change()
    {
        var steps = new[]
        {
            Step(0, "user", "first question", turn: 1),
            Step(1, "message", "answer", turn: 1, request: 1),
            Step(2, "user", "second question", turn: 2),
        };

        var rows = TrajectoryLedger.Build(steps, false, false, null);

        // Layout mirrors the native ledger: user input sits directly under the Turn (it belongs
        // to no assistant request), while assistant steps are nested under their Request header.
        Assert.Equal(6, rows.Count);
        Assert.Equal(TrajectoryRowKind.TurnHeader, rows[0].RowKind);
        Assert.Equal("Turn 1", rows[0].Label);
        Assert.Equal(TrajectoryRowKind.Step, rows[1].RowKind);
        Assert.Equal(TrajectoryRowKind.RequestHeader, rows[2].RowKind);
        Assert.Equal("Request #1", rows[2].Label);
        Assert.Equal(TrajectoryRowKind.Step, rows[3].RowKind);
        Assert.Equal(TrajectoryRowKind.TurnHeader, rows[4].RowKind);
        Assert.Equal("Turn 2", rows[4].Label);
        Assert.Equal(TrajectoryRowKind.Step, rows[5].RowKind);
    }

    [Fact]
    public void Request_header_reemits_for_a_new_turn()
    {
        // A turn boundary must reset the request counter, otherwise the second turn's first
        // request would be swallowed because it repeats an index the previous turn already used.
        var steps = new[]
        {
            Step(0, "message", "a", turn: 1, request: 1),
            Step(1, "message", "b", turn: 2, request: 1),
        };

        var rows = TrajectoryLedger.Build(steps, false, false, null);

        Assert.Equal(2, rows.Count(r => r.RowKind == TrajectoryRowKind.RequestHeader));
        Assert.Equal(2, rows.Count(r => r.Label == "Request #1"));
    }

    [Fact]
    public void Steps_are_indented_deeper_than_headers()
    {
        var steps = new[] { Step(0, "user", "q", turn: 1) };
        var rows = TrajectoryLedger.Build(steps, false, false, null);

        Assert.Equal(0, rows[0].Indent);                                    // Turn
        Assert.Equal(2, Assert.Single(rows, r => r.RowKind == TrajectoryRowKind.Step).Indent);
    }

    [Fact]
    public void Collapse_turns_hides_step_rows()
    {
        var steps = new[]
        {
            Step(0, "user", "q1", turn: 1),
            Step(1, "message", "a1", turn: 1, request: 1),
            Step(2, "user", "q2", turn: 2),
        };

        var rows = TrajectoryLedger.Build(steps, collapseTurns: true, false, null);

        Assert.All(rows, r => Assert.Equal(TrajectoryRowKind.TurnHeader, r.RowKind));
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Collapse_calls_hides_only_tool_rows()
    {
        var steps = new[]
        {
            Step(0, "user", "q", turn: 1),
            Step(1, "tool", "bash ls", turn: 1, request: 1),
            Step(2, "message", "done", turn: 1, request: 1),
        };

        var rows = TrajectoryLedger.Build(steps, false, collapseCalls: true, null);

        Assert.DoesNotContain(rows, r => r.RowKind == TrajectoryRowKind.Step && r.Step!.Kind == "tool");
        Assert.Contains(rows, r => r.RowKind == TrajectoryRowKind.Step && r.Step!.Kind == "message");
    }

    [Fact]
    public void Search_filters_steps_but_keeps_their_headers()
    {
        var steps = new[]
        {
            Step(0, "user", "alpha question", turn: 1),
            Step(1, "tool", "beta command", turn: 1, request: 1),
        };

        var rows = TrajectoryLedger.Build(steps, false, false, "beta");

        // The matched step survives and is still nested under its Turn/Request headers.
        Assert.Equal(3, rows.Count);
        Assert.Equal(TrajectoryRowKind.TurnHeader, rows[0].RowKind);
        Assert.Equal(TrajectoryRowKind.RequestHeader, rows[1].RowKind);
        Assert.Equal("beta command", rows[2].Summary);
    }

    [Fact]
    public void Search_is_case_insensitive_and_matches_kind_and_call_id()
    {
        var steps = new[]
        {
            Step(0, "tool", "list files", turn: 1, callId: "call-42"),
            Step(1, "user", "hello", turn: 1),
        };

        // Only the tool step matches, and it carries no request index, so the output is just
        // the Turn header plus that step.
        Assert.Equal(2, TrajectoryLedger.Build(steps, false, false, "CALL-42").Count);
        Assert.Single(TrajectoryLedger.Build(steps, false, false, "TOOL"),
                      r => r.RowKind == TrajectoryRowKind.Step);
        Assert.DoesNotContain(TrajectoryLedger.Build(steps, false, false, "zzz-no-match"),
                              r => r.RowKind == TrajectoryRowKind.Step);
    }

    [Fact]
    public void Search_overrides_turn_collapse_so_matches_stay_visible()
    {
        // Hiding a matched row behind a collapsed group would defeat the point of searching.
        var steps = new[] { Step(0, "user", "needle", turn: 1) };

        var rows = TrajectoryLedger.Build(steps, collapseTurns: true, false, "needle");

        Assert.Single(rows, r => r.RowKind == TrajectoryRowKind.Step);
    }

    [Fact]
    public void Summary_is_collapsed_to_a_single_line()
    {
        // Variable row heights would defeat the fixed-height virtualization the list uses.
        var steps = new[] { Step(0, "message", "line one\nline two\r\nline three", turn: 1) };

        var row = Assert.Single(TrajectoryLedger.Build(steps, false, false, null),
                                r => r.RowKind == TrajectoryRowKind.Step);

        Assert.DoesNotContain("\n", row.Summary);
        Assert.DoesNotContain("\r", row.Summary);
        Assert.Equal("line one line two line three", row.Summary);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(820L, "820ms")]
    [InlineData(999L, "999ms")]
    [InlineData(1000L, "1.0s")]
    [InlineData(3400L, "3.4s")]
    public void Duration_is_formatted_culture_independently(long? ms, string expected)
        => Assert.Equal(expected, TrajectoryLedger.FormatDuration(ms));

    [Fact]
    public void Assistant_kind_is_labelled_assistant()
    {
        // Native parity: the wire kind is "message" but the ledger shows ASSISTANT.
        Assert.Equal("ASSISTANT", TrajectoryLedger.KindLabel("message"));
        Assert.Equal("TOOL", TrajectoryLedger.KindLabel("tool"));
        Assert.Equal("CONTEXT", TrajectoryLedger.KindLabel("context"));
    }

    [Fact]
    public void Error_flag_and_duration_reach_the_row()
    {
        var steps = new[] { Step(0, "tool", "boom", turn: 1, isError: true, durationMs: 2500) };

        var row = Assert.Single(TrajectoryLedger.Build(steps, false, false, null),
                                r => r.RowKind == TrajectoryRowKind.Step);

        Assert.True(row.IsError);
        Assert.Equal("2.5s", row.Meta);
    }

    // ── Public helpers used by the WPF detail-window titles ──────────────────────────────────

    [Fact]
    public void ToSingleLine_collapses_newlines_and_runs_of_whitespace()
    {
        // Detail-window titles are built from message bodies, which are multi-line; a newline in a
        // window title would break layout, so this must be robust.
        Assert.Equal("one two three", TrajectoryLedger.ToSingleLine("one\n two\r\n   three"));
        Assert.Equal("", TrajectoryLedger.ToSingleLine(null));
        Assert.Equal("", TrajectoryLedger.ToSingleLine("   \n\t "));
    }

    [Fact]
    public void TruncateTo_appends_an_ellipsis_only_when_it_actually_cuts()
    {
        Assert.Equal("abc", TrajectoryLedger.TruncateTo("abc", 10));
        Assert.Equal("abc…", TrajectoryLedger.TruncateTo("abcdef", 3));
        Assert.Equal("", TrajectoryLedger.TruncateTo(null, 5));
        // Exact-length text must NOT get an ellipsis (it wasn't cut).
        Assert.Equal("abcde", TrajectoryLedger.TruncateTo("abcde", 5));
    }

    [Fact]
    public void Empty_trajectory_produces_no_rows()
    {
        var rows = TrajectoryLedger.Build(new List<SessionFold.TrajectoryStep>(), false, false, null);
        Assert.Empty(rows);
    }

    // ── Stability for live-source callers (snapshot contract) ────────────────────────────────

    /// <summary>
    /// Regression (2026-08-31): when callers hand a <see cref="System.Collections.Generic.List{T}"/>
    /// wrapped as <c>IReadOnlyList</c> to Build, a host that appends to the SAME list during
    /// Build throws "Collection was modified; enumeration may not execute." (the WPF catch
    /// path seen in dsh-client.log at 2026-08-31 15:29:14). To pin the fix in the caller, this
    /// test documents the safe call shape: pass an array (the snapshot), not the live list.
    /// </summary>
    [Fact]
    public void Build_accepts_arrays_safely()
    {
        var steps = new SessionFold.TrajectoryStep[]
        {
            Step(0, "user", "a", turn: 1),
            Step(1, "message", "b", turn: 1, request: 1),
        };

        // Passing an array (the snapshot shape) must never enumerate the source live list, so
        // there is nothing that can race with an out-of-band Append. This test only documents
        // the API contract; the WPF-side fix lives in RebuildTrajectoryRows.
        var rows = TrajectoryLedger.Build(steps, false, false, null);
        Assert.Equal(4, rows.Count);   // Turn header + Request header + 2 steps
    }
}
