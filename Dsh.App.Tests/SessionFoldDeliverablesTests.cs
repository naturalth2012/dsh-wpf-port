using System.Text.Json;
using Dsh.App;
using Xunit;

namespace Dsh.App.Tests;

/// <summary>
/// P2-5 regression tests: the fold derives produced files (deliverables) from tool views
/// by render intent (a diff card, or a generic card whose kind is "edit"), matching the
/// host's turn-deliverables reader — never from the closing prose.
/// </summary>
public class SessionFoldDeliverablesTests
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

    private const string ToolCall =
        """{"type":"tool/call","seq":1,"data":{"callId":"c1","name":"edit","view":{"card":"generic","kind":"edit","locations":[{"path":"/a/one.ts"},{"path":"/a/two.ts"}]}}}""";

    [Fact]
    public void Generic_edit_view_yields_produced_paths_in_order()
    {
        var fold = Fold(
            """{"type":"tool/call","seq":1,"data":{"callId":"c1","name":"edit","view":{"card":"generic","kind":"edit","locations":[{"path":"/a/one.ts"},{"path":"/a/two.ts"}]}}}""",
            """{"type":"tool/result","seq":2,"data":{"turn":1,"message":{"source":{"callId":"c1"},"content":[{"type":"tool-result","toolCallId":"c1"}]}}}""");

        Assert.False(fold.Rows.Any(r => r.Role == "error"), "fold added an error row: "
            + string.Join("; ", fold.Rows.Where(r => r.Role == "error").Select(r => r.Text)));
        Assert.True(fold.Deliverables.Count == 2, "deliverables: "
            + string.Join(";", fold.Deliverables.Select(d => d.Path)));
        Assert.Equal("/a/one.ts", fold.Deliverables[0].Path);
        Assert.Equal("/a/two.ts", fold.Deliverables[1].Path);
        Assert.Equal(2, fold.Deliverables[0].Seq);
    }

    [Fact]
    public void Diff_view_yields_produced_paths()
    {
        var fold = Fold(
            """{"type":"tool/call","seq":1,"data":{"callId":"c1","name":"apply_patch","view":{"card":"diff","locations":[{"path":"/a/patch.ts"}]}}}""",
            """{"type":"tool/result","seq":2,"data":{"turn":1,"message":{"source":{"callId":"c1"},"content":[{"type":"tool-result","toolCallId":"c1"}]}}}""");

        Assert.Equal("/a/patch.ts", Assert.Single(fold.Deliverables).Path);
    }

    [Fact]
    public void Failed_call_produces_no_paths()
    {
        var fold = Fold(
            """{"type":"tool/call","seq":1,"data":{"callId":"c1","name":"edit","view":{"card":"generic","kind":"edit","locations":[{"path":"/a/x.ts"}]}}}""",
            """{"type":"tool/result","seq":2,"data":{"turn":1,"message":{"source":{"callId":"c1"},"content":[{"type":"tool-result","toolCallId":"c1","isError":true}]}}}""");

        Assert.Empty(fold.Deliverables);
    }

    [Fact]
    public void Non_mutation_view_produces_no_paths()
    {
        // A read (generic, kind read) and a terminal run produce nothing to open.
        var fold = Fold(
            """{"type":"tool/call","seq":1,"data":{"callId":"c1","name":"read","view":{"card":"generic","kind":"read","locations":[{"path":"/a/x.ts"}]}}}""",
            """{"type":"tool/result","seq":2,"data":{"turn":1,"message":{"source":{"callId":"c1"},"content":[{"type":"tool-result","toolCallId":"c1"}]}}}""");

        Assert.Empty(fold.Deliverables);
    }

    [Fact]
    public void Duplicate_path_within_turn_is_deduped()
    {
        var fold = Fold(
            """{"type":"turn/start","seq":0,"data":{"turn":1}}""",
            """{"type":"tool/call","seq":1,"data":{"callId":"c1","name":"edit","view":{"card":"generic","kind":"edit","locations":[{"path":"/a/x.ts"}]}}}""",
            """{"type":"tool/result","seq":2,"data":{"turn":1,"message":{"source":{"callId":"c1"},"content":[{"type":"tool-result","toolCallId":"c1"}]}}}""",
            """{"type":"tool/call","seq":3,"data":{"callId":"c2","name":"edit","view":{"card":"generic","kind":"edit","locations":[{"path":"/a/x.ts"},{"path":"/a/y.ts"}]}}}""",
            """{"type":"tool/result","seq":4,"data":{"turn":1,"message":{"source":{"callId":"c2"},"content":[{"type":"tool-result","toolCallId":"c2"}]}}}""");

        Assert.Equal(2, fold.Deliverables.Count);
        Assert.Equal("/a/x.ts", fold.Deliverables[0].Path);
        Assert.Equal("/a/y.ts", fold.Deliverables[1].Path);
    }

    [Fact]
    public void New_turn_resets_deliverables()
    {
        var fold = Fold(
            """{"type":"turn/start","seq":0,"data":{"turn":1}}""",
            ToolCall,
            """{"type":"tool/result","seq":2,"data":{"turn":1,"message":{"source":{"callId":"c1"},"content":[{"type":"tool-result","toolCallId":"c1"}]}}}""",
            """{"type":"turn/start","seq":5,"data":{"turn":2}}""");

        Assert.Empty(fold.Deliverables);
    }
}
