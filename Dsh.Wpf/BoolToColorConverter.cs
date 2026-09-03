using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Dsh.Wpf;

/// <summary>
/// Maps <see cref="bool"/> to a foreground brush for status text: <c>true</c> → green
/// (running/ready), <c>false</c> → red (stopped/failed).
/// </summary>
public sealed class BoolToColorConverter : IValueConverter
{
    private static readonly Brush TrueBrush = new SolidColorBrush(Color.FromRgb(5, 150, 105));
    private static readonly Brush FalseBrush = new SolidColorBrush(Color.FromRgb(185, 28, 28));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && b ? TrueBrush : FalseBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
