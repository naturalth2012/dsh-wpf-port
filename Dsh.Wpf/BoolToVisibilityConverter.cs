using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Dsh.Wpf;

/// <summary>
/// Parameterized bool-to-visibility converter.
/// <list type="bullet">
///   <item>No parameter: <c>true</c> → Visible, <c>false</c> → Collapsed.</item>
///   <item>Parameter <c>"invert"</c>: <c>true</c> → Collapsed, <c>false</c> → Visible.</item>
///   <item>Parameter <c>"notEmpty"</c>: treats the value as a string; non-null/non-empty → Visible, else Collapsed.</item>
/// </list>
/// Replaces <see cref="InverseBoolToVisibilityConverter"/> and <see cref="StringNotEmptyToVisibilityConverter"/>.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var param = parameter as string;

        if (string.Equals(param, "notEmpty", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
        }

        bool b = value is bool bv && bv;
        if (string.Equals(param, "invert", StringComparison.OrdinalIgnoreCase))
        {
            return b ? Visibility.Collapsed : Visibility.Visible;
        }
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
