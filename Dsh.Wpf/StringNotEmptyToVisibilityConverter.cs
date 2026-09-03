using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Dsh.Wpf;

/// <summary>
/// Visible when the bound string is non-null and non-empty, Collapsed otherwise. The inverse of
/// <see cref="EmptyStringToVisibilityConverter"/> — used to show a prominent header/title bar only
/// while there is an actual value (e.g. the selected session title).
/// </summary>
public sealed class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
