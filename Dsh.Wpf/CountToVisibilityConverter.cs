using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Dsh.App;

namespace Dsh.Wpf;

/// <summary>
/// Converts a collection (or a count) to a <see cref="Visibility"/> for empty-state placeholders.
/// <para>
/// Right-panel tabs (Jobs / Todo / Queue) are populated by host pushes, so having no data is a
/// perfectly normal state — e.g. no sub-agent is running, or nothing is queued. Without a
/// placeholder those tabs simply look broken.
/// </para>
/// Converts <c>0</c> / an empty collection / <c>null</c> to <see cref="Visibility.Visible"/>
/// (show the placeholder) and anything else to <see cref="Visibility.Collapsed"/>.
/// </summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // The emptiness rule lives in Dsh.App.EmptyState so it is unit-testable without WPF
        // (Dsh.App.Tests cannot reference the WPF assemblies); this class only maps the result
        // onto a Visibility.
        return EmptyState.IsEmpty(value) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        // Fully qualify: the project also references System.Windows.Forms, which has its own Binding.
        => System.Windows.Data.Binding.DoNothing;
}

/// <summary>
/// Opposite-of-empty converter: <see>Visible</see> when the collection has items, Collapsed when
/// empty/null. Used by the produced-files bar (P2-5), which must stay hidden when no file has been
/// produced yet, otherwise a glaring bar sits at the bottom of the panel with nothing in it.
/// <para>
/// Separate type from <see cref="CountToVisibilityConverter"/> on purpose: a future maintainer
/// reading the XAML needs to tell at a glance whether the intent is "show when empty" or
/// "show when non-empty" — a generic converter with a parameter would still look identical at
/// the call site, and a single wrong binding here is exactly what kept the produced-files bar
/// from rendering correctly.
/// </para>
/// </summary>
public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        // Deliberately NOT inverted EmptyState.IsEmpty at the call site: a plain `!IsEmpty(x)`
        // would silently flip back to the buggy semantics if someone "simplifies" it later.
        // Expressing intent positively (HasAny(x)) keeps the right thing obvious.
        => EmptyState.HasAny(value) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}
