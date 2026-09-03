using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Dsh.Wpf;

/// <summary>
/// Maps a notification severity string ("error"/"success"/"info") to a foreground brush for
/// the transient status-bar notification (P0-1): error → red, success → green, info → blue.
/// Any unknown value falls back to the info (blue) brush.
/// </summary>
public sealed class NotificationColorConverter : IValueConverter
{
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(185, 28, 28));
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(5, 150, 105));
    private static readonly Brush InfoBrush = new SolidColorBrush(Color.FromRgb(29, 78, 216));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            "error" => ErrorBrush,
            "success" => SuccessBrush,
            _ => InfoBrush,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
