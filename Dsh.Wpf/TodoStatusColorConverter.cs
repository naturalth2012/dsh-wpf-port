using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Dsh.Wpf;

/// <summary>Maps a todo status to a color: pending gray, in_progress blue, done green.</summary>
public sealed class TodoStatusColorConverter : IValueConverter
{
    private static readonly Brush Pending = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
    private static readonly Brush InProgress = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
    private static readonly Brush Done = new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return (value as string) switch
        {
            "in_progress" => InProgress,
            "done" => Done,
            _ => Pending,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
