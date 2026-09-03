using System.Text.Json;
using Dsh.App;
using Dsh.Viewer;

namespace Dsh.Viewer.Tests;

/// <summary>
/// S4c export regression tests: <see cref="TranscriptExporter.ToMarkdown"/> renders role-labelled
/// sections and nests tool cards, without dropping content. Pure string logic, headless.
/// </summary>
public class TranscriptExporterTests
{
    private static List<SessionFold.Row> Fold(params string[] recordJsons)
    {
        var fold = new SessionFold();
        fold.Reset();
        foreach (var line in recordJsons)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var ev = JsonDocument.Parse(line).RootElement;
            foreach (var e in ChunkRowExpander.Decode(ev)) fold.Fold(e);
        }
        fold.MaterializePendingRow();
        return fold.Rows;
    }

    [Fact]
    public void Markdown_contains_user_and_assistant_sections()
    {
        var rows = Fold(
            """{"type":"user/message","seq":1,"time":1,"data":{"content":[{"type":"text","text":"ping"}],"role":"user","id":"u"}}""",
            """{"type":"turn/start","seq":2,"time":2,"data":{"turn":1}}""",
            """{"type":"assistant/chunk","seq":3,"time":3,"data":{"turn":1,"step":0,"chunk":{"type":"text-delta","index":0,"text":"pong"}}}""",
            """{"type":"assistant/message","seq":4,"time":4,"data":{"turn":1,"step":0,"message":{"content":[{"type":"text","text":"pong"}],"id":"a"}}}""");
        string md = TranscriptExporter.ToMarkdown("demo", rows);

        Assert.Contains("demo", md);
        Assert.Contains("## 🧑 用户", md);
        Assert.Contains("ping", md);
        Assert.Contains("## 🤖 Assistant", md);
        Assert.Contains("pong", md);
    }

    [Fact]
    public void Markdown_embeds_tool_name_status_and_arguments()
    {
        var rows = Fold(
            """{"type":"tool/call","seq":1,"time":1,"data":{"callId":"c1","name":"bash","arguments":"{\"cmd\":\"ls\"}"}}""",
            """{"type":"tool/result","seq":2,"time":2,"data":{"message":{"content":[{"type":"tool-result","toolCallId":"c1","content":[{"type":"text","text":"file.txt"}],"isError":false}]}}}""");
        string md = TranscriptExporter.ToMarkdown("t", rows);

        Assert.Contains("bash", md);
        Assert.Contains("done", md);     // result status
        Assert.Contains("file.txt", md); // tool output
        Assert.Contains("ls", md);       // arguments
    }
}
