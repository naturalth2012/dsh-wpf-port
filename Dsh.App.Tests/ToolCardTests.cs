using Dsh.App;
using Xunit;

namespace Dsh.App.Tests;

/// <summary>
/// Tests for the per-type tool card helpers (P0-3): <see cref="SessionFold.DiffLine"/>
/// classification and <see cref="SessionFold.ToolCallNode.DiffLines"/> projection, which feed
/// the diff tool card's add/remove/header/context coloring.
/// </summary>
public class ToolCardTests
{
    [Theory]
    [InlineData("+added line", "add")]
    [InlineData("-removed line", "remove")]
    [InlineData("@@ -1,3 +1,4 @@", "header")]
    [InlineData("diff --git a/b.txt b/b.txt", "header")]
    [InlineData("Index: b.txt", "header")]
    [InlineData("+++ b/b.txt", "header")]
    [InlineData("--- a/b.txt", "header")]
    [InlineData("  context line", "context")]
    public void DiffLine_classifies_leading_character(string line, string expectedKind)
    {
        Assert.Equal(expectedKind, SessionFold.DiffLine.FromText(line).Kind);
    }

    [Fact]
    public void ToolCallNode_DiffLines_is_null_when_no_output()
    {
        var node = new SessionFold.ToolCallNode("c1", "diff", null, "done", null);
        Assert.Null(node.DiffLines);
    }

    [Fact]
    public void ToolCallNode_DiffLines_splits_output_and_classifies_each_line()
    {
        var node = new SessionFold.ToolCallNode(
            "c1", "diff", null, "done",
            "diff --git a/x b/x\n@@ -1 +1 @@\n-old\n+new");

        var lines = node.DiffLines;
        Assert.NotNull(lines);
        Assert.Equal(4, lines!.Count);
        Assert.Equal("header", lines[0].Kind);
        Assert.Equal("header", lines[1].Kind);
        Assert.Equal("remove", lines[2].Kind);
        Assert.Equal("add", lines[3].Kind);
        Assert.Equal("-old", lines[2].Text);
        Assert.Equal("+new", lines[3].Text);
    }
}
