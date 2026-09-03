namespace Dsh.App.Tests;

/// <summary>
/// Intentionally empty.
///
/// <see cref="Dsh.Wpf.CountToVisibilityConverter"/> is a WPF <c>IValueConverter</c>, and this
/// test project does not reference the WPF assemblies (see <c>Dsh.App.Tests.csproj</c>), so it
/// cannot be instantiated here. Its emptiness rule was therefore extracted to
/// <see cref="Dsh.App.EmptyState"/> — which IS testable from this project — and is covered by
/// <c>EmptyStateTests</c> instead. The converter itself is now a one-line mapping from that rule
/// onto <c>Visibility</c>, so there is nothing meaningful left to unit-test.
/// </summary>
public static class CountToVisibilityConverterTests
{
}
