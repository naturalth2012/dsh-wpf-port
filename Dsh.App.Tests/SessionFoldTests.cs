using System.Text.Json;
using Dsh.App;
using Xunit;

namespace Dsh.App.Tests;

/// <summary>
/// Unit tests for <see cref="SessionFold"/>: fold a wire <c>SessionEvent</c> payload into
/// a surface transcript, asserting token-level streaming (assistant/chunk deltas), turn
/// finalization, and tool/user interleaving. Uses wire-shape JSON, not mocks.
/// </summary>
public class SessionFoldTests
{
    [Fact]
    public void Folds_token_deltas_into_one_assistant_row()
    {
        var fold = new SessionFold();

        Assert.True(fold.Fold(El(TextDelta("Hello"))));
        Assert.True(fold.Fold(El(TextDelta(", world"))));
        fold.Fold(El(Markup("assistant/message")));

        var row = Assert.Single(fold.Rows);
        Assert.Equal("assistant", row.Role);
        Assert.Equal("Hello, world", row.Text);
    }

    [Fact]
    public void Folds_user_then_assistant_rows_in_order()
    {
        var fold = new SessionFold();

        fold.Fold(El(UserMessage("hi")));
        fold.Fold(El(TextDelta("hello!")));
        fold.Fold(El(Markup("assistant/message")));

        Assert.Equal(2, fold.Rows.Count);
        Assert.Equal("user", fold.Rows[0].Role);
        Assert.Equal("hi", fold.Rows[0].Text);
        Assert.Equal("assistant", fold.Rows[1].Role);
        Assert.Equal("hello!", fold.Rows[1].Text);
    }

    [Fact]
    public void Reasoning_delta_goes_into_reasoning_slot_not_text()
    {
        var fold = new SessionFold();

        fold.Fold(El(Chunk("thought")));
        fold.Fold(El(Markup("assistant/message")));

        var row = Assert.Single(fold.Rows);
        Assert.Equal("thought", row.Reasoning);
        Assert.Equal("", row.Text);
    }

    [Fact]
    public void Text_and_reasoning_stream_separately()
    {
        var fold = new SessionFold();

        fold.Fold(El(Chunk("think first")));
        fold.Fold(El(TextDelta(" answer")));
        fold.Fold(El(Markup("assistant/message")));

        var row = Assert.Single(fold.Rows);
        Assert.Equal("think first", row.Reasoning);
        Assert.Equal(" answer", row.Text);
    }

    [Fact]
    public void Assistant_message_content_blocks_are_authoritative()
    {
        var fold = new SessionFold();

        // Chunks may arrive partial; the final assistant/message content array wins.
        fold.Fold(El(Chunk("stale")));
        fold.Fold(El(AssistantMessage("final answer", "final think")));

        var row = Assert.Single(fold.Rows);
        Assert.Equal("final answer", row.Text);
        Assert.Equal("final think", row.Reasoning);
    }

    [Fact]
    public void Tool_call_inserts_a_tool_row()
    {
        var fold = new SessionFold();

        fold.Fold(El(ToolCall("bash")));
        var toolRow = Assert.Single(fold.Rows);
        Assert.Equal("tool", toolRow.Role);
        Assert.Equal("bash", toolRow.Tool!.Name);
        Assert.Equal("running", toolRow.Tool.Status);
    }

    [Fact]
    public void Unknown_event_is_ignored_without_changing_surface()
    {
        var fold = new SessionFold();

        Assert.False(fold.Fold(El(Markup("step/start"))));
        Assert.Empty(fold.Rows);
    }

    [Fact]
    public void Tool_call_and_result_pair_by_callId()
    {
        var fold = new SessionFold();

        fold.Fold(El(ToolCall("bash", "c1")));
        fold.Fold(El(ToolResult("c1", "done", isError: false)));

        var row = Assert.Single(fold.Rows);
        Assert.Equal("tool", row.Role);
        Assert.Equal("c1", row.Tool!.CallId);
        Assert.Equal("done", row.Tool.Status);
        Assert.Equal("done", row.Tool.Output);
    }

    [Fact]
    public void Tool_result_error_marks_status_error()
    {
        var fold = new SessionFold();

        fold.Fold(El(ToolCall("bash", "c2")));
        fold.Fold(El(ToolResult("c2", "boom", isError: true)));

        var row = Assert.Single(fold.Rows);
        Assert.Equal("error", row.Tool!.Status);
        Assert.Equal("boom", row.Tool.Output);
    }

    [Fact]
    public void Running_tool_has_no_output_yet()
    {
        var fold = new SessionFold();

        fold.Fold(El(ToolCall("fs.write", "c3")));

        var row = Assert.Single(fold.Rows);
        Assert.Equal("running", row.Tool!.Status);
        Assert.Null(row.Tool.Output);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>A reasoning (think) delta, distinct from text.</summary>
    private static string Chunk(string text) =>
        $"{{ \"type\": \"assistant/chunk\", \"turn\": 1, \"step\": 1, " +
        $"\"chunk\": {{ \"type\": \"reasoning-delta\", \"index\": 0, \"text\": \"{text}\" }} }}";

    private static string TextDelta(string text) =>
        $"{{ \"type\": \"assistant/chunk\", \"turn\": 1, \"step\": 1, " +
        $"\"chunk\": {{ \"type\": \"text-delta\", \"index\": 0, \"text\": \"{text}\" }} }}";

    private static string Markup(string type) =>
        $"{{ \"type\": \"{type}\", \"turn\": 1, \"step\": 1 }}";

    private static string AssistantMessage(string text, string? reasoning = null)
    {
        var blocks = reasoning is null
            ? $"[{{\"type\":\"text\",\"text\":\"{text}\"}}]"
            : $"[{{\"type\":\"reasoning\",\"text\":\"{reasoning}\"}},{{\"type\":\"text\",\"text\":\"{text}\"}}]";
        return $"{{ \"type\": \"assistant/message\", \"message\": {{ \"content\": {blocks} }} }}";
    }

    private static string UserMessage(string content) =>
        $"{{ \"type\": \"user/message\", \"content\": \"{content}\", \"source\": \"prompt\" }}";

    private static string ToolCall(string name, string callId = "c1") =>
        $"{{ \"type\": \"tool/call\", \"name\": \"{name}\", \"callId\": \"{callId}\", \"arguments\": \"\" }}";

    private static string ToolResult(string callId, string text, bool isError) =>
        $"{{ \"type\": \"tool/result\", \"turn\": 1, \"step\": 1, " +
        $"\"message\": {{ \"content\": [ {{ \"type\": \"tool-result\", \"toolCallId\": \"{callId}\", " +
        $"\"isError\": {isError.ToString().ToLowerInvariant()}, " +
        $"\"content\": [ {{ \"type\": \"text\", \"text\": \"{text}\" }} ] }} ] }} }}";

    // ── real-wire shape (SessionEvent = { type, seq, time, data:{...} }) ──────

    [Fact]
    public void Folds_real_wire_session_event_with_data_payload()
    {
        // A real session/event frame event: { type:"assistant/chunk", seq, time,
        // data:{ turn, step, chunk:{ type:"text-delta", index, text } } }.
        const string wire =
            "{\"type\":\"assistant/chunk\",\"seq\":5,\"time\":1," +
            "\"data\":{\"turn\":1,\"step\":1," +
            "\"chunk\":{\"type\":\"text-delta\",\"index\":0,\"text\":\"real\"}}}";

        var fold = new SessionFold();
        fold.Fold(JsonDocument.Parse(wire).RootElement);
        fold.Fold(JsonDocument.Parse("{\"type\":\"assistant/message\",\"seq\":6,\"time\":1,\"data\":{\"turn\":1,\"step\":1,\"message\":{\"content\":[]}}}").RootElement);

        var row = Assert.Single(fold.Rows);
        Assert.Equal("real", row.Text);
    }

    [Fact]
    public void ToolCallBlock_builds_recursive_tree_from_subCalls()
    {
        // A settled tool-call block owning two sub-calls (one settled, one running).
        const string block =
            "{\"kind\":\"tool-result\",\"callId\":\"root\",\"isError\":false," +
            "\"call\":{\"name\":\"run_plan\",\"argsRaw\":\"{}\"}," +
            "\"content\":[{\"type\":\"text\",\"text\":\"done\"}]," +
            "\"subCalls\":[" +
            "{\"callId\":\"child1\",\"name\":\"bash\",\"argsRaw\":\"{\\\"cmd\\\":\\\"ls\\\"}\"}," +
            "{\"kind\":\"tool-result\",\"callId\":\"child2\",\"isError\":true," +
            "\"call\":{\"name\":\"read\",\"argsRaw\":\"\"}," +
            "\"content\":[{\"type\":\"text\",\"text\":\"boom\"}],\"subCalls\":[]}" +
            "]}";

        var node = SessionFold.ToolCallNode.FromBlock(JsonDocument.Parse(block).RootElement);

        Assert.Equal("run_plan", node.Name);
        Assert.Equal("done", node.Status);
        Assert.Equal("done", node.Output);
        Assert.Equal(2, node.Children!.Count);

        Assert.Equal("bash", node.Children[0].Name);
        Assert.Equal("running", node.Children[0].Status);

        Assert.Equal("read", node.Children[1].Name);
        Assert.Equal("error", node.Children[1].Status);
        Assert.Equal("boom", node.Children[1].Output);
    }

    [Fact]
    public void AssistantMessage_content_tool_call_builds_tree_row()
    {
        const string wire =
            "{\"type\":\"assistant/message\",\"seq\":7,\"time\":1," +
            "\"data\":{\"turn\":1,\"step\":1,\"message\":{\"content\":[" +
            "{\"type\":\"tool-call\",\"callId\":\"r1\",\"name\":\"root_tool\",\"argsRaw\":\"{}\",\"subCalls\":[]}" +
            "]}}}";

        var fold = new SessionFold();
        fold.Fold(JsonDocument.Parse(wire).RootElement);

        var toolRow = Assert.Single(fold.Rows);
        Assert.Equal("tool", toolRow.Role);
        Assert.Equal("root_tool", toolRow.Tool!.Name);
    }

    /// <summary>
    /// Regression: an assistant/message whose content blocks carry no usable text/reasoning
    /// (e.g. a bare `{type:"text"}` with no text field) must not crash with an out-of-range
    /// row access when it is the first message of a session (Rows is empty).
    /// </summary>
    [Fact]
    public void Assistant_message_with_no_usable_content_does_not_crash_empty_rows()
    {
        const string wire =
            "{\"type\":\"assistant/message\",\"seq\":7,\"time\":1," +
            "\"data\":{\"turn\":1,\"step\":1,\"message\":{\"content\":[" +
            "{\"type\":\"text\"}" + // text block with no `text` field
            "]}}}";

        var fold = new SessionFold();
        fold.Fold(JsonDocument.Parse(wire).RootElement);

        // No crash; the surface may end up empty.
        Assert.True(fold.Rows.Count == 0);
    }

    [Fact]
    public void Turn_start_and_end_insert_separator_rows()
    {
        const string turnStart = "{\"type\":\"turn/start\",\"seq\":1,\"time\":1,\"data\":{\"turn\":2}}";
        const string turnEnd = "{\"type\":\"turn/end\",\"seq\":2,\"time\":1,\"data\":{\"turn\":2,\"reason\":\"done\"}}";

        var fold = new SessionFold();
        Assert.True(fold.Fold(JsonDocument.Parse(turnStart).RootElement));
        fold.Fold(JsonDocument.Parse(turnEnd).RootElement);

        Assert.Equal(2, fold.Rows.Count);
        Assert.Equal("turn", fold.Rows[0].Role);
        Assert.Contains("2", fold.Rows[0].Text);
        Assert.Equal("turn", fold.Rows[1].Role);
        Assert.Contains("done", fold.Rows[1].Text);
    }

    [Fact]
    public void Turn_round_trip_groups_user_and_assistant()
    {
        const string turnStart = "{\"type\":\"turn/start\",\"seq\":1,\"time\":1,\"data\":{\"turn\":3}}";
        const string userMsg = "{\"type\":\"user/message\",\"seq\":2,\"time\":1,\"data\":{\"content\":\"ask\",\"source\":\"prompt\"}}";
        const string textDelta = "{\"type\":\"assistant/chunk\",\"seq\":3,\"time\":1,\"data\":{\"turn\":3,\"step\":1,\"chunk\":{\"type\":\"text-delta\",\"index\":0,\"text\":\"reply\"}}}";
        const string turnEnd = "{\"type\":\"turn/end\",\"seq\":4,\"time\":1,\"data\":{\"turn\":3,\"reason\":\"done\"}}";

        var fold = new SessionFold();
        fold.Fold(JsonDocument.Parse(turnStart).RootElement);
        fold.Fold(JsonDocument.Parse(userMsg).RootElement);
        fold.Fold(JsonDocument.Parse(textDelta).RootElement);
        fold.Fold(JsonDocument.Parse(turnEnd).RootElement);

        Assert.Equal(4, fold.Rows.Count);
        Assert.Equal("turn", fold.Rows[0].Role);
        Assert.Equal("user", fold.Rows[1].Role);
        Assert.Equal("assistant", fold.Rows[2].Role);
        Assert.Equal("reply", fold.Rows[2].Text);
        Assert.Equal("turn", fold.Rows[3].Role);
    }

    /// <summary>
    /// P0-1 regression guard: AppendChunk must NOT materialize the row on every chunk (that was
    /// the O(n²) copy), but the accumulated text MUST still be visible once it is read. The view
    /// model calls MaterializePendingRow() before rendering; this test pins both halves of that
    /// contract so a future change can't silently reintroduce the per-chunk copy or drop text.
    /// </summary>
    [Fact]
    public void Streamed_chunks_are_visible_after_MaterializePendingRow()
    {
        var fold = new SessionFold();
        // Several deltas into one assistant message — no finalizing event in between.
        foreach (var piece in new[] { "Hel", "lo, ", "stream", "ing ", "world" })
        {
            fold.Fold(JsonDocument.Parse(TextDelta(piece)).RootElement);
        }

        var row = Assert.Single(fold.Rows);
        Assert.Equal("assistant", row.Role);
        // Before materialization the row still carries the empty placeholder the fold created.
        Assert.Equal("", row.Text);

        // The VM materializes once, right before rendering.
        fold.MaterializePendingRow();

        Assert.Equal("Hello, streaming world", fold.Rows[0].Text);
        // Idempotent: a second call must not duplicate or corrupt anything.
        fold.MaterializePendingRow();
        Assert.Equal("Hello, streaming world", fold.Rows[0].Text);
        Assert.Single(fold.Rows);
    }

    /// <summary>
    /// P0-1: starting a new assistant row (or any buffer-clearing transition) must commit the
    /// previous row's streamed text instead of losing it when the buffers are cleared.
    /// </summary>
    [Fact]
    public void Starting_a_new_assistant_row_preserves_previous_streamed_text()
    {
        var fold = new SessionFold();
        fold.Fold(JsonDocument.Parse(TextDelta("first")).RootElement);
        fold.Fold(JsonDocument.Parse(Markup("turn/end")).RootElement);
        fold.Fold(JsonDocument.Parse(TextDelta("second")).RootElement);

        // turn/end finalizes, which commits "first" before the buffers are cleared.
        Assert.Equal("first", fold.Rows[0].Text);

        fold.Fold(JsonDocument.Parse(Markup("turn/end")).RootElement);

        // turn/end appends its own "turn" row, so the assistant message is not the last row.
        // Layout: [assistant "first", turn, assistant "second", turn].
        var assistantRows = fold.Rows.Where(r => r.Role == "assistant").ToArray();
        Assert.Equal(2, assistantRows.Length);
        Assert.Equal("first", assistantRows[0].Text);
        Assert.Equal("second", assistantRows[1].Text);
    }

    [Fact]
    public void Todo_write_captures_whole_list_snapshot()
    {
        const string wire =
            "{\"type\":\"todo/write\",\"seq\":9,\"time\":1,\"data\":{\"todos\":[" +
            "{\"content\":\"task a\",\"status\":\"in_progress\"}," +
            "{\"content\":\"task b\",\"status\":\"pending\"}," +
            "{\"content\":\"task c\",\"status\":\"done\"}" +
            "]}}";

        var fold = new SessionFold();
        Assert.True(fold.Fold(JsonDocument.Parse(wire).RootElement));

        Assert.Equal(3, fold.Todos.Count);
        Assert.Equal("task a", fold.Todos[0].Content);
        Assert.Equal("in_progress", fold.Todos[0].Status);
        Assert.Equal("done", fold.Todos[2].Status);
    }

    [Fact]
    public void Todo_write_latest_write_wins()
    {
        const string first =
            "{\"type\":\"todo/write\",\"seq\":9,\"time\":1,\"data\":{\"todos\":[{\"content\":\"a\",\"status\":\"pending\"}]}}";
        const string second =
            "{\"type\":\"todo/write\",\"seq\":10,\"time\":1,\"data\":{\"todos\":[{\"content\":\"a\",\"status\":\"done\"}]}}";

        var fold = new SessionFold();
        fold.Fold(JsonDocument.Parse(first).RootElement);
        fold.Fold(JsonDocument.Parse(second).RootElement);

        var todo = Assert.Single(fold.Todos);
        Assert.Equal("done", todo.Status);
    }
}
