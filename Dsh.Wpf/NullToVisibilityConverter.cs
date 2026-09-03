using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Dsh.Wpf;

/// <summary>Converts a null reference to <see cref="Visibility.Collapsed"/>, else Visible.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
