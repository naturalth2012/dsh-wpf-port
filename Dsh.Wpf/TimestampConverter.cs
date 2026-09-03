using System.Globalization;
using System.Windows.Data;
using Dsh.App.Services;

namespace Dsh.Wpf;

/// <summary>
/// Converts an event timestamp (epoch ms, long) to a local, culture-aware "HH:mm" string for
/// hover timestamps. Non-numeric input yields empty. Uses <see cref="Localization.Culture"/> so
/// the format follows the active UI language.
/// </summary>
public sealed class TimestampConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long ms || ms <= 0) return "";
        try
        {
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
            return dt.ToString("HH:mm", Localization.Culture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "";
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
