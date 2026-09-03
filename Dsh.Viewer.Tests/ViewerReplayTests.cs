using System.IO;
using System.Text.Json;
using Dsh.App;

namespace Dsh.Viewer.Tests;

/// <summary>
/// Replay regression tests (os/03 L1): the offline path replays a disk <c>session.jsonl[.zstd]</c>
/// through the shared <see cref="SessionFold"/> the same way <c>Dsh.Viewer.MainWindow.LoadSelected</c>
/// does — decode each JSONL record, expand chunk rows, then <c>fold.Fold</c> each event. These tests
/// pin down what the offline replay actually yields (rows, tool pairing, token metrics) so the
/// L3/L4 feature scope rests on verified behavior rather than assumption.
/// </summary>
public class ViewerReplayTests
{
    /// <summary>Replay a set of disk-style JSONL records through the fold, mirroring LoadSelected.</summary>
    private static SessionFold Replay(params string[] recordJsons)
    {
        var fold = new SessionFold();
        fold.Reset();
        foreach (var line in recordJsons)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var ev = JsonDocument.Parse(line).RootElement;
            // LoadSelected expands chunk rows first; only the expanded events reach the fold.
            var expanded = Dsh.Viewer.ChunkRowExpander.Decode(ev);
            foreach (var e in expanded) fold.Fold(e);
        }
        return fold;
    }

    // ── header + seed events must not throw / pollute surface ────────────────────────────────

    [Fact]
    public void Replay_header_and_metadata_events_do_not_crash_or_emit_rows()
    {
        var fold = Replay(
            """{"type":"session","version":0,"id":"session-x","createdAt":1,"cwd":"D:\\x"}""",
            """{"type":"permission/preset","seq":0,"time":1,"data":{"preset":"workspace-write"}}""",
            """{"type":"sandbox/mode","seq":1,"time":2,"data":{"mode":"workspace-write"}}""",
            """{"type":"approval/policy","seq":2,"time":3,"data":{"policy":"ask"}}""",
            """{"type":"session/end-seed","seq":3,"time":4,"data":{}}""");
        // None of these metadata events produce chat surface.
        Assert.Empty(fold.Rows);
    }

    // ── user message + assistant chunk deltas produce a surface transcript ───────────────────

    [Fact]
    public void Replay_user_and_assistant_chunks_build_a_transcript()
    {
        var fold = Replay(
            """{"type":"user/message","seq":10,"time":100,"data":{"content":[{"type":"text","text":"1+1?"}],"role":"user","id":"m1"}}""",
            """{"type":"turn/start","seq":11,"time":101,"data":{"turn":1}}""",
            """{"type":"text-chunks","seq0":12,"time0":102,"data":{"turn":1,"step":0,"index":0,"dt":[5],"texts":["2"]}}""");

        // turn/start renders a turn-separator row (Role=turn); user + assistant deltas follow.
        var user = fold.Rows.Single(r => r.Role == "user");
        Assert.Contains("1+1?", user.Text);

        // Streamed chunks buffer into a pending row until finalized (an assistant/message frame) or
        // until MaterializePendingRow() is flushed. LoadSelected must call MaterializePendingRow()
        // after reading the file or mid-stream assistant text never lands in Row.Text.
        var assistant = fold.Rows.Single(r => r.Role == "assistant");
        Assert.True(string.IsNullOrWhiteSpace(assistant.Text), "text is still buffered, not materialized");
        fold.MaterializePendingRow();
        Assert.Contains("2", fold.Rows.Single(r => r.Role == "assistant").Text);

        Assert.Contains(fold.Rows, r => r.Role == "turn");
    }

    // ── tool/call + tool/result pair into a tool row by callId (no view needed) ──────────────

    [Fact]
    public void Replay_tool_call_and_result_pair_into_one_tool_row()
    {
        var fold = Replay(
            """{"type":"user/message","seq":20,"time":200,"data":{"content":"run it","role":"user","id":"m2"}}""",
            """{"type":"assistant/chunk","seq":21,"time":201,"data":{"turn":1,"step":0,"chunk":{"type":"tool-call-delta","index":0,"id":"call_9","name":"bash","argumentsDelta":"{}"}}}""",
            """{"type":"tool/call","seq":22,"time":202,"data":{"turn":1,"step":0,"callId":"call_9","name":"bash","arguments":"{}"}}""",
            """{"type":"tool/result","seq":23,"time":203,"data":{"turn":1,"step":0,"message":{"content":[{"type":"tool-result","toolCallId":"call_9","content":[{"type":"text","text":"ok"}],"isError":false}]}}}""");

        var toolRows = fold.Rows.Where(r => r.Role == "tool").ToList();
        Assert.Single(toolRows);
        var tool = toolRows[0].Tool!;
        Assert.Equal("bash", tool.Name);
        Assert.Equal("call_9", tool.CallId);
        Assert.Equal("done", tool.Status);
    }

    // ── token usage persisted on disk reaches the trajectory ─────────────────────────────────

    [Fact]
    public void Replay_assistant_message_usage_reaches_trajectory_tokens()
    {
        var fold = Replay(
            """{"type":"user/message","seq":30,"time":300,"data":{"content":"q","role":"user","id":"m3"}}""",
            """{"type":"assistant/message","seq":31,"time":301,"data":{"turn":1,"step":0,"message":{"content":[{"type":"text","text":"done"}],"role":"assistant","id":"a1"},"usage":{"inputTokens":1433,"outputTokens":125,"cacheReadTokens":6336}}}""");

        var assistantStep = fold.Trajectory.Single(s => s.Kind == "message");
        Assert.Equal(1433, assistantStep.InputTokens);
        Assert.Equal(125, assistantStep.OutputTokens);
        Assert.Equal(6336, assistantStep.CacheReadTokens);
    }

    [Fact]
    public void Replay_flat_token_counts_are_also_read()
    {
        // Some hosts place the counts at the top level of `data` rather than under `usage`.
        var fold = Replay(
            """{"type":"assistant/message","seq":40,"time":400,"data":{"turn":1,"step":0,"message":{"content":[{"type":"text","text":"hi"}],"id":"a2"},"inputTokens":11,"outputTokens":7}}""");

        var step = fold.Trajectory.Single(s => s.Kind == "message");
        Assert.Equal(11, step.InputTokens);
        Assert.Equal(7, step.OutputTokens);
    }

    // ── step/start adds no surface row; turn/start+end render only turn-group separators ─────

    [Fact]
    public void Replay_step_start_does_not_add_a_message_row()
    {
        var fold = Replay(
            """{"type":"turn/start","seq":50,"time":500,"data":{"turn":1}}""",
            """{"type":"step/start","seq":51,"time":501,"data":{"step":0,"intent":"reply"}}""");
        // step/start must not fabricate a user/assistant/tool message row.
        Assert.DoesNotContain(fold.Rows, r => r.Role is "user" or "assistant" or "tool" or "error");
    }

    // ── regression: LoadSelected must materialize streaming text before copying Rows ──────────
    // Mirrors the fixed MainWindow.LoadSelected pipeline (read file → decode → expand → fold →
    // MaterializePendingRow) so a real session whose assistant text arrives only via text-chunks
    // (no assistant/message finalizer) still shows its text.

    [Fact]
    public void File_load_materializes_streamed_assistant_text()
    {
        string path = Path.Combine(Path.GetTempPath(), "dshviewer-load-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            File.WriteAllText(path,
                """{"type":"user/message","seq":10,"time":100,"data":{"content":[{"type":"text","text":"hi"}],"role":"user","id":"m"}}""" + "\n"
                + """{"type":"text-chunks","seq0":11,"time0":101,"data":{"turn":1,"step":0,"index":0,"dt":[],"texts":["hello there"]}}""" + "\n");

            var fold = new Dsh.App.SessionFold();
            fold.Reset();
            foreach (var line in Dsh.Viewer.ZstdReader.ReadLines(path, isZstd: false))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var ev = JsonDocument.Parse(line).RootElement;
                foreach (var e in Dsh.Viewer.ChunkRowExpander.Decode(ev)) fold.Fold(e);
            }
            fold.MaterializePendingRow();   // ← the LoadSelected fix

            var assistant = fold.Rows.Single(r => r.Role == "assistant");
            Assert.Contains("hello there", assistant.Text);
        }
        finally { File.Delete(path); }
    }

    // ── regression: SessionFold.Rows accumulates across Reset() ───────────────────────────────
    // Root cause of "only the first session loads": LoadSelected reused a single fold and called
    // Reset(), but Reset() does NOT clear Rows. The fix (matching the online client) is one fresh
    // fold per session. This test locks the invariant so nobody reintroduces the shared instance.

    [Fact]
    public void Reusing_one_fold_across_reset_accumulates_rows()
    {
        var fold = new SessionFold();
        fold.Reset();
        var ev = JsonDocument.Parse(
            """{"type":"user/message","seq":1,"time":1,"data":{"content":"a","role":"user","id":"u1"}}""").RootElement;
        fold.Fold(ev);

        int afterFirst = fold.Rows.Count;
        Assert.True(afterFirst >= 1);

        fold.Reset();                 // a "new session" on the SAME instance
        int afterReset = fold.Rows.Count;
        Assert.True(afterReset >= afterFirst,
            "Reset() does not clear Rows, so reusing one fold across sessions accumulates stale rows");
    }
}
