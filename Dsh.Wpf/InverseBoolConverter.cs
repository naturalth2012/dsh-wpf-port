using System.Globalization;
using System.Windows.Data;

namespace Dsh.Wpf;

/// <summary>Logical NOT for <see cref="bool"/> bindings (e.g. <c>IsEnabled</c>).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;
}
