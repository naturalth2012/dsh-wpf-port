using Dsh.App;
using Xunit;

namespace Dsh.App.Tests;

/// <summary>
/// Tests for <see cref="SnippetHighlighter"/> (P2-13): client-side highlight segmentation of
/// plain search snippets returned by <c>session.search</c>.
/// </summary>
public class SnippetHighlighterTests
{
    [Fact]
    public void Null_snippet_returns_empty()
    {
        Assert.Empty(SnippetHighlighter.Highlight(null, "x"));
    }

    [Fact]
    public void Empty_snippet_returns_empty()
    {
        Assert.Empty(SnippetHighlighter.Highlight("", "x"));
    }

    [Fact]
    public void Blank_query_returns_single_plain_segment()
    {
        var segs = SnippetHighlighter.Highlight("hello world", "   ");
        var s = Assert.Single(segs);
        Assert.Equal("hello world", s.Text);
        Assert.False(s.IsMatch);
    }

    [Fact]
    public void No_match_returns_single_plain_segment()
    {
        var segs = SnippetHighlighter.Highlight("hello world", "xyz");
        var s = Assert.Single(segs);
        Assert.Equal("hello world", s.Text);
        Assert.False(s.IsMatch);
    }

    [Fact]
    public void Single_match_is_flagged()
    {
        var segs = SnippetHighlighter.Highlight("the QUICK brown fox", "quick");
        // plain + match + plain
        Assert.Equal(3, segs.Count);
        Assert.False(segs[0].IsMatch);
        Assert.Equal("QUICK", segs[1].Text);
        Assert.True(segs[1].IsMatch);
        Assert.False(segs[2].IsMatch);
    }

    [Fact]
    public void Multiple_matches_all_flagged()
    {
        var segs = SnippetHighlighter.Highlight("cat and CAT and cat", "cat");
        Assert.Equal(5, segs.Count); // match, plain, match, plain, match
        Assert.True(segs[0].IsMatch);   // "cat"
        Assert.True(segs[2].IsMatch);   // "CAT"
        Assert.Equal("CAT", segs[2].Text);
        Assert.True(segs[4].IsMatch);   // "cat"
        Assert.False(segs[1].IsMatch);
        Assert.False(segs[3].IsMatch);
    }

    [Fact]
    public void Repeated_non_overlapping_matches()
    {
        // "ab ab ab" with query "ab": three separate matches, case-insensitive on the middle.
        var segs = SnippetHighlighter.Highlight("ab Ab ab", "ab");
        Assert.Equal(5, segs.Count); // match, space, match, space, match
        Assert.True(segs[0].IsMatch);
        Assert.True(segs[2].IsMatch);
        Assert.Equal("Ab", segs[2].Text);
        Assert.True(segs[4].IsMatch);
    }

    [Fact]
    public void Adjacent_matches_merge()
    {
        var segs = SnippetHighlighter.Highlight("aaaa", "aa");
        // "aa"@0 and "aa"@2 are adjacent → merge into one [0,4)
        Assert.Single(segs);
        Assert.True(segs[0].IsMatch);
        Assert.Equal("aaaa", segs[0].Text);
    }
}
