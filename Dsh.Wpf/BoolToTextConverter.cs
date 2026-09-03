using System.Globalization;
using System.Windows.Data;
using Dsh.App.Services;
using Loc = Dsh.App.Services.Localization;

namespace Dsh.Wpf;

/// <summary>
/// Renders a <see cref="bool"/> as a localized toggle label: <c>true</c> → Collapse, <c>false</c> → Expand.
/// Used by the collapsible service-log header button.
/// </summary>
public sealed class BoolToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && b ? Loc.Get("Log.Collapse") : Loc.Get("Log.Expand");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
