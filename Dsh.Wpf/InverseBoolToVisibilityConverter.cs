using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Dsh.Wpf;

/// <summary>
/// Inverts <see cref="bool"/> to <see cref="Visibility"/>: <c>false</c> → Visible,
/// <c>true</c> → Collapsed. Used to hide the ordinary question affordance while a
/// plan-review item is showing its approval card (and vice-versa).
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is bool bv && bv;
        return b ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
