using Dsh.Viewer;

namespace Dsh.Viewer.Tests;

/// <summary>
/// Regression for "show session title instead of the raw session id": a <see cref="SessionNode"/>
/// starts with a compact short-id label and, once <c>LoadSelected</c> discovers the real title from
/// a <c>session/title</c> event, <see cref="SessionNode.SetTitle"/> updates DisplayName so both the
/// tree and the header surface the human-readable title. Blank titles are ignored (fallback id).
/// </summary>
public class SessionNodeTests
{
    private static SessionNode Node(string id = "session-0123456789abcdef") =>
        new("C:\\x\\" + id + "\\session.jsonl", "ws", id, isZstd: false);

    [Fact]
    public void ShortId_includes_unique_guid_segment_not_just_shared_prefix()
    {
        // The first 16 chars span the shared 'session-' prefix and 8 hex chars of the GUID, so
        // every un-loaded session shows a distinguishing label rather than the identical
        // 'session-' short id (which made the tree appear to repeat rows).
        var a = Node("session-0123456789abcdef");
        var b = Node("session-fedcba9876543210");
        Assert.Equal("session-01234567", a.ShortId);
        Assert.Equal("session-fedcba98", b.ShortId);
        Assert.NotEqual(a.ShortId, b.ShortId);
        Assert.Equal(a.ShortId, a.DisplayName);
    }

    [Fact]
    public void SetTitle_updates_display_to_the_title()
    {
        var n = Node();
        n.SetTitle("fix session switching");
        Assert.Equal("fix session switching", n.DisplayName);
    }

    [Fact]
    public void SetTitle_ignores_blank_title_keeping_short_id()
    {
        var n = Node();
        n.SetTitle("   ");
        n.SetTitle(null);
        n.SetTitle("");
        Assert.Equal(n.ShortId, n.DisplayName);
    }

    [Fact]
    public void SetTitle_trims_whitespace()
    {
        var n = Node();
        n.SetTitle("  hello  ");
        Assert.Equal("hello", n.DisplayName);
    }

    [Fact]
    public void Raise_property_changed_when_title_set()
    {
        var n = Node();
        string? fired = null;
        n.PropertyChanged += (_, e) => fired = e.PropertyName;
        n.SetTitle("t");
        Assert.Equal(nameof(SessionNode.DisplayName), fired);
    }
}