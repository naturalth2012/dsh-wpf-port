using System.Collections.ObjectModel;
using Xunit;

namespace Dsh.App.Tests;

/// <summary>
/// Contract tests for <see cref="EmptyState"/>, the rule behind the right-panel empty-state
/// placeholders (Jobs / Todo / Queue / Settings).
///
/// The critical guarantee is the FAIL-SAFE direction: an empty collection shows the placeholder,
/// but anything unrecognised must NOT — if a binding ever breaks we show an empty panel rather
/// than silently hiding real content. A missing hint is acceptable; invisible data is not.
/// </summary>
public sealed class EmptyStateTests
{
    [Fact]
    public void Zero_count_is_empty()
        => Assert.True(EmptyState.IsEmpty(0));

    [Fact]
    public void Empty_collections_are_empty()
    {
        Assert.True(EmptyState.IsEmpty(new ObservableCollection<string>()));
        Assert.True(EmptyState.IsEmpty(new string[0]));
    }

    [Fact]
    public void Null_binding_is_treated_as_empty()
    {
        // An unresolved binding degrades to "show the hint" rather than to a broken-looking panel.
        Assert.True(EmptyState.IsEmpty(null));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(572)]
    public void Non_zero_count_is_not_empty(int count)
        => Assert.False(EmptyState.IsEmpty(count));

    [Fact]
    public void Non_empty_collections_are_not_empty()
    {
        Assert.False(EmptyState.IsEmpty(new ObservableCollection<string> { "a" }));
        Assert.False(EmptyState.IsEmpty(new[] { "a", "b" }));
    }

    [Fact]
    public void Unrecognised_value_is_not_empty()
    {
        // Fail-safe: never hide real content when the shape is unexpected.
        Assert.False(EmptyState.IsEmpty("not a count"));
        Assert.False(EmptyState.IsEmpty(new object()));
    }

    // ── HasAny: the positive form, used by UI that should HIDE when empty ────────────────────

    [Fact]
    public void HasAny_returns_true_for_non_empty_collections()
    {
        Assert.True(EmptyState.HasAny(new ObservableCollection<string> { "a" }));
        Assert.True(EmptyState.HasAny(new[] { "a" }));
        Assert.True(EmptyState.HasAny(1));
        Assert.True(EmptyState.HasAny(572));     // typical mid-session step count
    }

    [Fact]
    public void HasAny_returns_false_for_empty_collections_and_zero()
    {
        Assert.False(EmptyState.HasAny(null));
        Assert.False(EmptyState.HasAny(0));
        Assert.False(EmptyState.HasAny(new ObservableCollection<string>()));
    }

    [Fact]
    public void HasAny_fails_safe_to_false_for_unrecognised_shapes()
    {
        // Match IsEmpty's fail-safe: unrecognised shape returns false (== empty). The reason is
        // a small one: if a binding ever breaks and we have NO IDEA whether the collection has
        // items, hiding the panel is safer than flashing an empty one at the user. The cost of a
        // missing produced-files bar is small; the cost of pretending to be working when we are
        // not is larger (a file that "doesn't exist" is harder to debug than no bar at all).
        Assert.False(EmptyState.HasAny("not a count"));
        Assert.False(EmptyState.HasAny(new object()));
    }
}
