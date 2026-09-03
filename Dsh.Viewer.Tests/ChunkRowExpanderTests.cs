using System.Text.Json;
using Dsh.Viewer;

namespace Dsh.Viewer.Tests;

/// <summary>
/// L1 chunk-row regression tests (os/03): <c>ChunkRowExpander.Decode</c> must expand the durable
/// storage rows (<c>text-chunks</c>/<c>reasoning-chunks</c>/<c>tool-call-chunks</c>) back into the
/// exact <c>assistant/chunk</c> events <c>SessionFold</c> expects — the faithful inverse of the
/// upstream <c>expandRow</c>. Anything that is not one of these three rows passes through verbatim.
/// </summary>
public class ChunkRowExpanderTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ── pass-through of non-chunk rows ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("{\"type\":\"user/message\",\"seq\":1,\"data\":{\"content\":\"hi\"}}")]
    [InlineData("{\"type\":\"turn/start\",\"seq\":2,\"data\":{\"turn\":1}}")]
    [InlineData("{\"type\":\"session\",\"id\":\"s\",\"cwd\":\"C:\\\\x\"}")]
    [InlineData("\"bare string value\"")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"data\":\"no type field\"}")]
    public void Decode_passes_through_non_chunk_rows(string json)
    {
        var value = Parse(json);
        var result = ChunkRowExpander.Decode(value);
        Assert.Single(result);
        Assert.Equal(value.GetRawText(), result[0].GetRawText());
    }

    // ── text-chunks ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TextChunks_expand_to_assistant_text_chunks_with_seq_time_advance()
    {
        var row = Parse(
            """{"type":"text-chunks","seq0":100,"time0":1000,"data":{"turn":1,"step":0,"index":0,"dt":[10,20],"texts":["hel","lo","!"]}}""");
        var result = ChunkRowExpander.Decode(row);

        Assert.Equal(3, result.Length);
        for (int i = 0; i < 3; i++)
        {
            var e = result[i];
            Assert.Equal("assistant/chunk", e.GetProperty("type").GetString());
            Assert.Equal(100 + i, e.GetProperty("seq").GetInt64());
            // time0 + prefix dt (chunk k accumulates dt[0..k-1]).
            long expectedTime = 1000 + (i switch { 0 => 0L, 1 => 10L, 2 => 30L, _ => 0L });
            Assert.Equal(expectedTime, e.GetProperty("time").GetInt64());
            var data = e.GetProperty("data");
            Assert.Equal(1, data.GetProperty("turn").GetInt64());
            Assert.Equal(0, data.GetProperty("step").GetInt64());
            var chunk = data.GetProperty("chunk");
            Assert.Equal("text-delta", chunk.GetProperty("type").GetString());
            Assert.Equal(0, chunk.GetProperty("index").GetInt64());
            Assert.Equal(new[] { "hel", "lo", "!" }[i], chunk.GetProperty("text").GetString());
        }
    }

    // ── reasoning-chunks ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReasoningChunks_expand_to_assistant_reasoning_chunks()
    {
        var row = Parse(
            """{"type":"reasoning-chunks","seq0":5,"time0":500,"data":{"turn":2,"step":1,"index":0,"dt":[3],"texts":["think"]}}""");
        var result = ChunkRowExpander.Decode(row);

        Assert.Single(result);
        var chunk = result[0].GetProperty("data").GetProperty("chunk");
        Assert.Equal("reasoning-delta", chunk.GetProperty("type").GetString());
        Assert.Equal("think", chunk.GetProperty("text").GetString());
        Assert.Equal(2, result[0].GetProperty("data").GetProperty("turn").GetInt64());
    }

    // ── tool-call-chunks ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToolCallChunks_expand_to_tool_call_delta_with_id_and_name()
    {
        var row = Parse(
            """{"type":"tool-call-chunks","seq0":9,"time0":900,"data":{"turn":1,"step":2,"index":3,"dt":[],"id":"call_1","name":"edit","args":["{\"path\":\"a.cs\"}"]}}""");
        var result = ChunkRowExpander.Decode(row);

        Assert.Single(result);
        var chunk = result[0].GetProperty("data").GetProperty("chunk");
        Assert.Equal("tool-call-delta", chunk.GetProperty("type").GetString());
        Assert.Equal(3, chunk.GetProperty("index").GetInt64());
        Assert.Equal("call_1", chunk.GetProperty("id").GetString());
        Assert.Equal("edit", chunk.GetProperty("name").GetString());
        Assert.Equal("{\"path\":\"a.cs\"}", chunk.GetProperty("argumentsDelta").GetString());
    }

    // ── robustness: rows omitting optional fields must not throw ─────────────────────────────
    // A default JsonElement (absent property) cannot be serialized by the old anonymous-object
    // path; the expander must instead omit those members and still default seq/time to 0.

    [Fact]
    public void Decode_row_omitting_optional_fields_does_not_throw()
    {
        // No seq0/time0/turn/step/index — only a single text member.
        var result = ChunkRowExpander.Decode(
            Parse("""{"type":"text-chunks","data":{"texts":["only"]}}"""));

        Assert.Single(result);
        var e = result[0];
        Assert.Equal("assistant/chunk", e.GetProperty("type").GetString());
        Assert.Equal(0, e.GetProperty("seq").GetInt64());
        Assert.Equal(0, e.GetProperty("time").GetInt64());
        var data = e.GetProperty("data");
        // Absent turn/step/index are omitted rather than crashing.
        Assert.False(data.TryGetProperty("turn", out _));
        Assert.False(data.TryGetProperty("step", out _));
        Assert.False(data.GetProperty("chunk").TryGetProperty("index", out _));
        Assert.Equal("text-delta", data.GetProperty("chunk").GetProperty("type").GetString());
        Assert.Equal("only", data.GetProperty("chunk").GetProperty("text").GetString());
    }

    [Fact]
    public void Decode_tool_call_row_omitting_turn_and_step_does_not_throw()
    {
        var result = ChunkRowExpander.Decode(
            Parse("""{"type":"tool-call-chunks","seq0":0,"time0":0,"data":{"index":0,"id":"c2","args":["x"]}}"""));
        Assert.Single(result);
        var chunk = result[0].GetProperty("data").GetProperty("chunk");
        Assert.Equal("tool-call-delta", chunk.GetProperty("type").GetString());
        Assert.Equal("c2", chunk.GetProperty("id").GetString());
        Assert.False(chunk.TryGetProperty("name", out _));
        Assert.Equal("x", chunk.GetProperty("argumentsDelta").GetString());
    }

    [Fact]
    public void Decode_empty_members_produces_no_events()
    {
        var row = Parse("""{"type":"text-chunks","seq0":0,"time0":0,"data":{"texts":[]}}""");
        Assert.Empty(ChunkRowExpander.Decode(row));
    }
}
