using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace Dsh.Wpf;

/// <summary>
/// Renders a session-status dot for a <see cref="SessionItem"/> (P1-2): a green dot when a
/// background job runs, a blue dot when the agent runs, an amber dot when awaiting an
/// interaction, or hidden when idle. Multi-value binding on Running/Waiting/JobRunning with a
/// green > blue > amber > idle precedence so the most specific activity wins.
/// </summary>
public sealed class SessionStatusDotConverter : IMultiValueConverter
{
    private static readonly Brush RunningBlue = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
    private static readonly Brush WaitingAmber = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06));
    private static readonly Brush JobGreen = new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69));

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool running = values.Length > 0 && values[0] is bool b1 && b1;
        bool waiting = values.Length > 1 && values[1] is bool b2 && b2;
        bool jobRunning = values.Length > 2 && values[2] is bool b3 && b3;

        if (jobRunning) return JobGreen;
        if (running) return RunningBlue;
        if (waiting) return WaitingAmber;
        return Brushes.Transparent;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
