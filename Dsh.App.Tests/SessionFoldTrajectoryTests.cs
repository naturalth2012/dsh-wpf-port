using System.Text.Json;
using Dsh.App;
using Xunit;

namespace Dsh.App.Tests;

/// <summary>
/// P2-6 regression tests: the fold records a step-by-step trajectory from surface events,
/// independent of the surface rows, so a UI can offer replay without losing the timeline.
/// </summary>
public class SessionFoldTrajectoryTests
{
    private static SessionFold Fold(params string[] eventJsons)
    {
        var fold = new SessionFold();
        foreach (var json in eventJsons)
        {
            fold.Fold(JsonDocument.Parse(json).RootElement);
        }
        return fold;
    }

    [Fact]
    public void Surface_events_produce_steps_in_order()
    {
        var fold = Fold(
            """{"type":"user/message","seq":1,"data":{"content":"hello"}}""",
            """{"type":"assistant/message","seq":2,"data":{"message":{"content":[{"type":"text","text":"hi"}]}}}""",
            """{"type":"tool/call","seq":3,"data":{"callId":"c1","name":"bash"}}""");

        Assert.Equal(3, fold.Trajectory.Count);
        Assert.Equal("user", fold.Trajectory[0].Kind);
        Assert.Equal("hello", fold.Trajectory[0].Text);
        // Native-parity kind for assistant output is "message" (rendered as ASSISTANT).
        Assert.Equal("message", fold.Trajectory[1].Kind);
        Assert.Equal("tool", fold.Trajectory[2].Kind);
        Assert.Equal("bash", fold.Trajectory[2].Text);
        // tool/call carries a callId, used to pair it with its tool/result.
        Assert.Equal("c1", fold.Trajectory[2].CallId);
    }

    [Fact]
    public void Steps_have_monotonic_indices_and_originating_seq()
    {
        var fold = Fold(
            """{"type":"turn/start","seq":10,"data":{"turn":1}}""",
            """{"type":"user/message","seq":11,"data":{"content":"q"}}""",
            """{"type":"turn/end","seq":12,"data":{"reason":"completed"}}""");

        // Native parity: turn/start and turn/end are GROUPING signals, not steps. Only the
        // user message produces a step, and it is tagged with the current turn index.
        Assert.Single(fold.Trajectory);
        Assert.Equal(0, fold.Trajectory[0].Index);
        Assert.Equal(11, fold.Trajectory[0].Seq);
        Assert.Equal(1, fold.Trajectory[0].TurnIndex);
    }

    [Fact]
    public void Turn_grouping_advances_and_tags_subsequent_steps()
    {
        // The native ledger groups as "Turn N" → "Request #N" → steps. Verify the counters.
        var fold = Fold(
            """{"type":"turn/start","seq":1,"data":{}}""",
            """{"type":"user/message","seq":2,"data":{"content":"first question"}}""",
            """{"type":"assistant/message","seq":3,"data":{"message":{"content":[{"type":"text","text":"answer"}]}}}""",
            """{"type":"turn/end","seq":4,"data":{}}""",
            """{"type":"turn/start","seq":5,"data":{}}""",
            """{"type":"user/message","seq":6,"data":{"content":"second question"}}""");

        // 3 steps: the two turn/start and the turn/end emit nothing (grouping only).
        Assert.Equal(3, fold.Trajectory.Count);

        // Turn 1: user step, then the assistant message opens request #1.
        Assert.Equal(1, fold.Trajectory[0].TurnIndex);
        Assert.Null(fold.Trajectory[0].RequestIndex);       // user step precedes any request
        Assert.Equal(1, fold.Trajectory[1].TurnIndex);
        Assert.Equal(1, fold.Trajectory[1].RequestIndex);

        // Turn 2: the request counter resets for the new turn, so the user step has none.
        Assert.Equal(2, fold.Trajectory[2].TurnIndex);
        Assert.Null(fold.Trajectory[2].RequestIndex);
    }

    [Fact]
    public void Injected_user_message_is_classified_as_context()
    {
        // Native parity: user/message with a non-user source is host-injected content and is
        // labelled CONTEXT rather than USER.
        var fold = Fold(
            """{"type":"user/message","seq":1,"data":{"source":{"kind":"injected"},"content":"context dump"}}""",
            """{"type":"user/message","seq":2,"data":{"source":{"kind":"user"},"content":"my question"}}""");

        Assert.Equal(2, fold.Trajectory.Count);
        Assert.Equal("context", fold.Trajectory[0].Kind);
        Assert.Equal("user", fold.Trajectory[1].Kind);
    }

    [Fact]
    public void Tool_result_error_flag_and_duration_are_captured()
    {
        var fold = Fold(
            """{"type":"tool/result","seq":1,"data":{"callId":"c9","name":"bash","isError":true,"durationMs":1234,"output":"boom"}}""");

        Assert.Single(fold.Trajectory);
        Assert.Equal("tool", fold.Trajectory[0].Kind);
        Assert.True(fold.Trajectory[0].IsError);
        Assert.Equal(1234, fold.Trajectory[0].DurationMs);
        Assert.Equal("c9", fold.Trajectory[0].CallId);
    }

    [Fact]
    public void Metadata_events_produce_context_steps()
    {
        // Behavior change (2026-08-30): config/metadata events now DO produce timeline steps
        // (kind "context"), matching the native-web trajectory, which shows CONTEXT entries.
        // Previously they fell through `default: return` and vanished from the WPF timeline —
        // a major reason it looked empty next to the web one.
        var fold = Fold(
            """{"type":"permission/preset","seq":1,"data":{"preset":"workspace-write"}}""",
            """{"type":"user/message","seq":2,"data":{"content":"x"}}""");

        Assert.Equal(2, fold.Trajectory.Count);
        Assert.Equal("context", fold.Trajectory[0].Kind);
        Assert.Contains("permission", fold.Trajectory[0].Text);
        Assert.Contains("workspace-write", fold.Trajectory[0].Text);
        Assert.Equal("user", fold.Trajectory[1].Kind);
    }

    [Fact]
    public void Assistant_steps_carry_real_content_not_a_placeholder()
    {
        // Regression: assistant/message used to store the literal "assistant message", making
        // every assistant step indistinguishable. The native-web timeline renders real content.
        // The wire nests the blocks under data.message.content.
        var fold = Fold(
            """{"type":"assistant/message","seq":1,"data":{"message":{"content":[{"type":"text","text":"1+1 = 2"}]}}}""");

        Assert.Single(fold.Trajectory);
        Assert.Equal("message", fold.Trajectory[0].Kind);
        Assert.Equal("1+1 = 2", fold.Trajectory[0].Text);
    }

    [Fact]
    public void Assistant_chunk_steps_carry_the_delta_text()
    {
        // Real wire: data.chunk = { type, index, text } — the shape AppendChunk reads.
        var fold = Fold(
            """{"type":"assistant/chunk","seq":1,"data":{"chunk":{"type":"text-delta","index":0,"text":"partial"}}}""");

        Assert.Single(fold.Trajectory);
        Assert.Equal("message", fold.Trajectory[0].Kind);
        Assert.Equal("partial", fold.Trajectory[0].Text);
    }

    [Fact]
    public void TrajectoryVersion_changes_on_every_mutation()
    {
        // Contract the WPF view model depends on: it republishes the trajectory to the UI only
        // when TrajectoryVersion changes, because `Trajectory` hands out the SAME live List
        // instance (so an INotifyPropertyChanged setter cannot detect mutations by reference).
        // If this ever stops bumping, the timeline silently freezes — the original blank bug.
        var fold = new SessionFold();
        int initial = fold.TrajectoryVersion;

        fold.Fold(JsonDocument.Parse("""{"type":"user/message","seq":1,"data":{"content":"a"}}""").RootElement);
        Assert.Equal(initial + 1, fold.TrajectoryVersion);

        fold.Fold(JsonDocument.Parse("""{"type":"user/message","seq":2,"data":{"content":"b"}}""").RootElement);
        Assert.Equal(initial + 2, fold.TrajectoryVersion);

        // Unknown events produce no step and must NOT bump the version.
        fold.Fold(JsonDocument.Parse("""{"type":"totally/unknown","seq":3,"data":{}}""").RootElement);
        Assert.Equal(initial + 2, fold.TrajectoryVersion);

        fold.Reset();
        Assert.Equal(initial + 3, fold.TrajectoryVersion);
        Assert.Empty(fold.Trajectory);
    }

    [Fact]
    public void Reset_Clears_Trajectory_And_Deliverables()
    {
        var fold = Fold(
            """{"type":"user/message","seq":1,"data":{"content":"hello"}}""",
            """{"type":"tool/call","seq":2,"data":{"callId":"c1","name":"edit","view":{"card":"generic","kind":"edit","locations":[{"path":"/a/x.ts"}]}}}""",
            """{"type":"tool/result","seq":3,"data":{"turn":1,"message":{"source":{"callId":"c1"},"content":[{"type":"tool-result","toolCallId":"c1"}]}}}""");

        Assert.Single(fold.Deliverables);
        Assert.NotEmpty(fold.Trajectory);

        fold.Reset();

        Assert.Empty(fold.Deliverables);
        Assert.Empty(fold.Trajectory);
    }
}
