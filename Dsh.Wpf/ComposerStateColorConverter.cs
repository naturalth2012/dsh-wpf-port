using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Dsh.Wpf;

/// <summary>
/// Maps a <see cref="ComposerMode"/> to a status-dot brush for the composer badge (P0-2):
/// idle → gray, running → blue, steering → purple. Unknown/other values fall back to gray.
/// </summary>
public sealed class ComposerStateColorConverter : IValueConverter
{
    private static readonly Brush IdleBrush = new SolidColorBrush(Color.FromRgb(156, 163, 175));
    private static readonly Brush RunningBrush = new SolidColorBrush(Color.FromRgb(29, 78, 216));
    private static readonly Brush SteeringBrush = new SolidColorBrush(Color.FromRgb(147, 51, 234));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            ComposerMode.Running => RunningBrush,
            ComposerMode.Steering => SteeringBrush,
            _ => IdleBrush,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
