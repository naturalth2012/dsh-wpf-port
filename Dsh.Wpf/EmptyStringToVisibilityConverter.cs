using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Dsh.Wpf;

/// <summary>
/// Collapses the target when the bound string is null or empty, otherwise Visible. Used for
/// optional status text (e.g. the P2-7 session-stats line) that should only occupy space when it
/// has content.
/// </summary>
public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
