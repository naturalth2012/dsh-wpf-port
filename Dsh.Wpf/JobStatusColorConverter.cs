using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Dsh.Wpf;

/// <summary>Maps a job status string to a foreground brush (running/queued vs done/error).</summary>
public sealed class JobStatusColorConverter : IValueConverter
{
    private static readonly Brush Running = new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69));
    private static readonly Brush Queued = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06));
    private static readonly Brush Done = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly Brush Error = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value as string;
        return status switch
        {
            "running" => Running,
            "stopping" => Queued,
            "queued" => Queued,
            "completed" => Done,
            "failed" => Error,
            "cancelled" => Error,
            _ => Done,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
