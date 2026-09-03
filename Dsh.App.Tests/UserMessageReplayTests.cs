using System.Text.Json;
using System.Linq;
using Xunit;

namespace Dsh.App.Tests;

/// <summary>
/// Regression tests for the user-message replay/de-dup contract:
/// <para>
/// WPF eagerly inserts a user row on SendPromptAsync (MessageId == null) so the composer
/// feels instant. The host then replays the authoritative <c>user/message</c> frame with the
/// real MessageId. <see cref="SessionFold"/> must MERGE the replay into the optimistic row
/// rather than appending a second bubble — otherwise the chat surface shows two identical
/// green user rows for a single send.
/// </para>
/// <para>
/// The screenshot that motivated this test showed two user bubbles around a "任务 1"
/// divider: the host had replayed <c>turn/start</c> BEFORE <c>user/message</c>, leaving the
/// optimistic user row no longer at the tail. The merge logic must walk back over the
/// intervening rows to find it.
/// </para>
/// </summary>
public sealed class UserMessageReplayTests
{
    private static readonly System.Text.Json.JsonElement EmptyView =
        System.Text.Json.JsonDocument.Parse("{}").RootElement;

    private static SessionFold.TrajectoryStep? Step(SessionFold fold, int index)
        => fold.Trajectory.Count > index ? fold.Trajectory[index] : null;

    [Fact]
    public void Optimistic_user_promoted_when_replay_is_tail_row()
    {
        // The "happy path" that was already working: the optimistic row is still last when the
        // replay arrives, so it is promoted in place (no duplicate).
        var fold = new SessionFold();
        fold.Rows.Add(new SessionFold.Row("user", "hello"));
        Assert.Null(fold.Rows[0].MessageId);

        fold.Fold(JsonDocument.Parse(
            """{"type":"user/message","seq":1,"data":{"id":"m1","content":[{"type":"text","text":"hello"}]}}"""
        ).RootElement, EmptyView);

        Assert.Single(fold.Rows);
        Assert.Equal("user", fold.Rows[0].Role);
        Assert.Equal("m1", fold.Rows[0].MessageId);
    }

    [Fact]
    public void Replay_after_turn_start_still_merges_into_optimistic_user()
    {
        // Bug reproduction (2026-08-31): the host replays turn/start before user/message,
        // pushing the optimistic user row off the tail. The old merge logic only checked
        // the last row, so it gave up and appended a duplicate bubble.
        var fold = new SessionFold();
        fold.Rows.Add(new SessionFold.Row("user", "prompt body"));

        fold.Fold(JsonDocument.Parse(
            """{"type":"turn/start","seq":1,"data":{"turn":1}}"""
        ).RootElement, EmptyView);
        fold.Fold(JsonDocument.Parse(
            """{"type":"user/message","seq":2,"data":{"id":"m1","content":[{"type":"text","text":"prompt body"}]}}"""
        ).RootElement, EmptyView);

        var userRows = fold.Rows.Where(r => r.Role == "user").ToList();
        Assert.Single(userRows);
        Assert.Equal("m1", userRows[0].MessageId);
    }

    [Fact]
    public void Replay_after_turn_start_then_tool_call_still_merges()
    {
        // The host may also schedule a tool call before echoing the prompt back (rare but
        // possible). Walk-back must skip the non-user rows.
        var fold = new SessionFold();
        fold.Rows.Add(new SessionFold.Row("user", "ask"));

        fold.Fold(JsonDocument.Parse("""{"type":"turn/start","seq":1,"data":{"turn":1}}""").RootElement, EmptyView);
        fold.Fold(JsonDocument.Parse(
            """{"type":"tool/call","seq":2,"data":{"callId":"c1","name":"search"}}"""
        ).RootElement, EmptyView);
        fold.Fold(JsonDocument.Parse(
            """{"type":"user/message","seq":3,"data":{"id":"m1","content":[{"type":"text","text":"ask"}]}}"""
        ).RootElement, EmptyView);

        Assert.Single(fold.Rows, r => r.Role == "user");
        Assert.Equal("m1", fold.Rows.First(r => r.Role == "user").MessageId);
    }

    [Fact]
    public void Replay_with_different_text_does_not_merge()
    {
        // Walk-back must not blindly merge any nearby optimistic row — the row's text has to
        // match. Otherwise two distinct back-to-back prompts would silently collapse into one.
        var fold = new SessionFold();
        fold.Rows.Add(new SessionFold.Row("user", "first"));

        fold.Fold(JsonDocument.Parse("""{"type":"turn/start","seq":1,"data":{"turn":1}}""").RootElement, EmptyView);
        fold.Fold(JsonDocument.Parse(
            """{"type":"user/message","seq":2,"data":{"id":"m2","content":[{"type":"text","text":"DIFFERENT"}]}}"""
        ).RootElement, EmptyView);

        // The replayed prompt is distinct: it must NOT merge with the older optimistic row,
        // so a fresh bubble appears (matching web behaviour — the user can see both prompts).
        var userRows = fold.Rows.Where(r => r.Role == "user").ToList();
        Assert.Equal(2, userRows.Count);
        Assert.Null(userRows[0].MessageId);   // still optimistic, from the older prompt
        Assert.Equal("m2", userRows[1].MessageId);
    }

    [Fact]
    public void Replay_after_a_settled_user_message_does_not_overlap_with_older_optimistic()
    {
        // The walk-back hits a previously authoritative user row first and stops — it must not
        // continue on to merge into an even older optimistic row from a different prompt.
        var fold = new SessionFold();
        fold.Rows.Add(new SessionFold.Row("user", "old"));   // still MessageId=null on this one
        // First prompt settles (its optimistic row gets its id).
        fold.Fold(JsonDocument.Parse(
            """{"type":"user/message","seq":1,"data":{"id":"m-old","content":[{"type":"text","text":"old"}]}}"""
        ).RootElement, EmptyView);
        // New optimistic prompt arrives.
        fold.Rows.Add(new SessionFold.Row("user", "new"));
        // And then turn/start before its replay.
        fold.Fold(JsonDocument.Parse("""{"type":"turn/start","seq":3,"data":{"turn":2}}""").RootElement, EmptyView);
        // Replay for the NEW prompt. Walking back: turn/start (skip) -> "new" user (MessageId
        // null, text matches) -> promote.
        fold.Fold(JsonDocument.Parse(
            """{"type":"user/message","seq":4,"data":{"id":"m-new","content":[{"type":"text","text":"new"}]}}"""
        ).RootElement, EmptyView);

        Assert.Equal("m-old", fold.Rows[0].MessageId);
        Assert.Equal("m-new", fold.Rows[1].MessageId);
    }
}