using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using Dsh.App.Services;

namespace Dsh.Wpf;

/// <summary>
/// XAML markup extension: <c>{loc:Loc Key=Connect}</c> resolves a localized string and
/// refreshes automatically when the language is switched (P2-8). The lookup logic lives in
/// <see cref="Localization"/> (Dsh.App) so it stays unit-testable without a WPF context.
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
        var binding = new System.Windows.Data.Binding("Culture")
        {
            Source = Dsh.App.Services.Localization.Signal,
            Mode = System.Windows.Data.BindingMode.OneWay,
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
