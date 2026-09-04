using System.Globalization;
using System.Windows.Data;

namespace Dsh.Wpf;

/// <summary>
/// Parameterized bool converter. Without parameter, passes the value through.
/// With parameter <c>"invert"</c>, returns the logical NOT.
/// </summary>
public sealed class BoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is bool bv && bv;
        return IsInvert(parameter) ? !b : b;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is bool bv && bv;
        return IsInvert(parameter) ? !b : b;
    }

    private static bool IsInvert(object? parameter) =>
        parameter is string s && string.Equals(s, "invert", StringComparison.OrdinalIgnoreCase);
}
