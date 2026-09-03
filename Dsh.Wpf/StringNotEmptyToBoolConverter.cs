using System.Globalization;
using System.Windows.Data;

namespace Dsh.Wpf;

/// <summary>
/// True when the bound string is non-null and non-empty. Used to drive a <see cref="Popup"/>
/// <c>IsOpen</c> (or other bool) from a string that may be empty, without the string occupying
/// layout space. Mirrors <see cref="EmptyStringToVisibilityConverter"/> but for bool targets.
/// </summary>
public sealed class StringNotEmptyToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        !string.IsNullOrEmpty(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
