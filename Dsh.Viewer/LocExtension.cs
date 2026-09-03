using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using Dsh.App.Services;

namespace Dsh.Viewer;

/// <summary>
/// XAML markup extension: <c>{loc:Loc Key=Viewer_...}</c> resolves a localized string from the
/// shared <see cref="Dsh.App"/> resource set and refreshes automatically when the language is
/// switched (via <see cref="Localization.Signal"/>). Mirrors <c>Dsh.Wpf.LocExtension</c> so the
/// standalone viewer stays self-contained (it does not reference Dsh.Wpf).
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding("Culture")
        {
            Source = Localization.Signal,
            Mode = BindingMode.OneWay,
            Converter = new LocConverter(Key),
        };
        return binding.ProvideValue(serviceProvider);
    }

    private sealed class LocConverter : IValueConverter
    {
        private readonly string _key;
        public LocConverter(string key) => _key = key;
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            Localization.Get(_key);
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
