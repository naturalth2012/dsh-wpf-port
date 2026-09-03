using Dsh.App;
using Xunit;

namespace Dsh.App.Tests;

/// <summary>
/// Tests for <see cref="MarkdownRenderer"/> (M0): projecting full GFM Markdown into
/// UI-bindable <see cref="MarkdownBlock"/>s. Covers headings, emphasis, lists, pipe tables,
/// fenced code, quotes, links, H4 file-mention links, and best-effort edge cases.
/// </summary>
public class MarkdownRendererTests
{
    [Fact]
    public void Empty_and_null_render_to_no_blocks()
    {
        Assert.Empty(MarkdownRenderer.Render(null));
        Assert.Empty(MarkdownRenderer.Render(""));
        Assert.Empty(MarkdownRenderer.Render("   \n  "));
    }

    [Fact]
    public void Plain_paragraph_produces_paragraph_block()
    {
        var blocks = MarkdownRenderer.Render("hello world");
        var b = Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.Paragraph, b.Kind);
        var run = Assert.Single(b.Runs!);
        Assert.Equal(InlineKind.Text, run.Kind);
        Assert.Equal("hello world", run.Text);
    }

    [Fact]
    public void Heading_carries_level_and_text()
    {
        var blocks = MarkdownRenderer.Render("# Title\n\n## Sub");
        Assert.Equal(2, blocks.Count);
        Assert.Equal(MarkdownBlockKind.Heading, blocks[0].Kind);
        Assert.Equal(1, blocks[0].Level);
        Assert.Equal("Title", Assert.Single(blocks[0].Runs!).Text);
        Assert.Equal(MarkdownBlockKind.Heading, blocks[1].Kind);
        Assert.Equal(2, blocks[1].Level);
    }

    [Fact]
    public void Bold_and_italic_are_emphasized()
    {
        var blocks = MarkdownRenderer.Render("**bold** and *italic*");
        var b = Assert.Single(blocks);
        Assert.Equal(InlineKind.Bold, b.Runs![0].Kind);
        Assert.Equal("bold", b.Runs[0].Text);
        Assert.Equal(InlineKind.Text, b.Runs[1].Kind);
        Assert.Equal(InlineKind.Italic, b.Runs[2].Kind);
        Assert.Equal("italic", b.Runs[2].Text);
    }

    [Fact]
    public void Fenced_code_captures_language_and_lines()
    {
        var blocks = MarkdownRenderer.Render("```csharp\nvar x = 1;\n```");
        var b = Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.Code, b.Kind);
        Assert.Equal("csharp", b.Language);
        Assert.Equal("var x = 1;", b.Text);
    }

    [Fact]
    public void Pipe_table_produces_header_and_rows()
    {
        var blocks = MarkdownRenderer.Render("| a | b |\n|---|---|\n| 1 | 2 |");
        var b = Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.Table, b.Kind);
        Assert.Equal(2, b.Rows!.Count);
        Assert.True(b.Rows[0].IsHeader);
        Assert.Equal("a", b.Rows[0].Cells[0]);
        Assert.Equal("b", b.Rows[0].Cells[1]);
        Assert.False(b.Rows[1].IsHeader);
        Assert.Equal("1", b.Rows[1].Cells[0]);
        Assert.Equal("2", b.Rows[1].Cells[1]);
    }

    [Fact]
    public void Unordered_list_produces_listitems_with_markers()
    {
        var blocks = MarkdownRenderer.Render("- one\n- two");
        Assert.Equal(2, blocks.Count);
        Assert.Equal(MarkdownBlockKind.ListItem, blocks[0].Kind);
        Assert.Equal("• ", blocks[0].ListMarker);
        Assert.Equal("one", Assert.Single(blocks[0].Runs!).Text);
        Assert.Equal("two", Assert.Single(blocks[1].Runs!).Text);
    }

    [Fact]
    public void Ordered_list_uses_incrementing_numbers()
    {
        var blocks = MarkdownRenderer.Render("1. first\n2. second");
        Assert.Equal(2, blocks.Count);
        Assert.Equal("1. ", blocks[0].ListMarker);
        Assert.Equal("2. ", blocks[1].ListMarker);
    }

    [Fact]
    public void Quote_block_is_extracted()
    {
        var blocks = MarkdownRenderer.Render("> a quoted line");
        var b = Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.Quote, b.Kind);
        Assert.Equal("a quoted line", Assert.Single(b.Runs!).Text);
    }

    [Fact]
    public void Link_is_emitted_with_url()
    {
        var blocks = MarkdownRenderer.Render("[docs](https://example.com)");
        var b = Assert.Single(blocks);
        var run = Assert.Single(b.Runs!);
        Assert.Equal(InlineKind.Link, run.Kind);
        Assert.Equal("docs", run.Text);
        Assert.Equal("https://example.com", run.Url);
    }

    [Fact]
    public void Thematic_break_produces_thematicbreak_block()
    {
        var blocks = MarkdownRenderer.Render("---");
        var b = Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.ThematicBreak, b.Kind);
    }

    [Fact]
    public void Backtick_file_path_is_clickable_file_mention()
    {
        // H4 regression: a backtick path should be projected as a File run (clickable).
        var blocks = MarkdownRenderer.Render("open `src/foo.cs`");
        var b = Assert.Single(blocks);
        Assert.Contains(b.Runs!, r => r.Kind == InlineKind.File && r.Text == "src/foo.cs");
    }

    [Fact]
    public void Inline_code_that_is_not_a_path_stays_inlinecode()
    {
        var blocks = MarkdownRenderer.Render("use `variable` here");
        var b = Assert.Single(blocks);
        Assert.Contains(b.Runs!, r => r.Kind == InlineKind.InlineCode && r.Text == "variable");
        Assert.DoesNotContain(b.Runs!, r => r.Kind == InlineKind.File);
    }

    [Fact]
    public void Unterminated_fence_is_kept_as_code_best_effort()
    {
        var blocks = MarkdownRenderer.Render("```\nvar x = 1;");
        var b = Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.Code, b.Kind);
        Assert.Contains("var x = 1;", b.Text);
    }

    [Fact]
    public void Multi_paragraph_markdown_flattens_into_ordered_blocks()
    {
        var blocks = MarkdownRenderer.Render("para one\n\npara two");
        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, b => Assert.Equal(MarkdownBlockKind.Paragraph, b.Kind));
    }
}
