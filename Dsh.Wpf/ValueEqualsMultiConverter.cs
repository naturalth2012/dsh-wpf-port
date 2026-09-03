using System.Globalization;
using System.Windows.Data;

namespace Dsh.Wpf;

/// <summary>
/// Multi-binding equality test: returns true when all bound values are equal (using
/// <see cref="object.Equals(object?, object?)"/>). Used to highlight the currently
/// selected permission preset chip by comparing its <c>Value</c> against the
/// session's <c>PermissionCurrent</c>.
/// </summary>
public sealed class ValueEqualsMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return false;
        var a = values[0];
        for (int i = 1; i < values.Length; i++)
        {
            if (!Equals(a, values[i])) return false;
        }
        return true;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
