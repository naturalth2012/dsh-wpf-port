using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace Dsh.App.Services;

/// <summary>
/// Runtime-localization helper (ML). Reads the embedded <c>Strings</c> resx for the active
/// culture and notifies the UI when the language changes. Supports multiple cultures via
/// standard resource fallback; an untranslated key falls back to zh-CN (the default UI
/// language), then to the key itself as a last resort.
/// </summary>
public static class Localization
{
    private static CultureInfo _culture = CultureInfo.CurrentUICulture;

    /// <summary>Notifier object used as the WPF binding source so {Loc} refreshes on language change.</summary>
    public static readonly Notifier Signal = new Notifier();

    /// <summary>Currently active UI culture (e.g. "zh-CN" or "fr").</summary>
    public static CultureInfo Culture
    {
        get => _culture;
        set
        {
            if (_culture.Name == value.Name) return;
            _culture = value;
            Signal.RaiseChanged();
        }
    }

    /// <summary>
    /// Language-driven UI font fallback chain (K4). Different scripts need different preferred
    /// faces: CJK (zh/ko) should lead with a CJK face and fall back through the Latin face;
    /// Latin languages (en/fr/de/es) lead with the UI face so glyphs render crisply. Each chain
    /// ends with Segoe UI so any leftover Latin glyphs resolve on Windows. Re-evaluate on
    /// <see cref="Culture"/> change and apply to the window root FontFamily (see MainViewModel).
    /// </summary>
    public static string UiFont
    {
        get
        {
            switch (_culture.TwoLetterISOLanguageName)
            {
                case "zh": return "Microsoft YaHei UI, 微软雅黑, Segoe UI";
                case "ko": return "Malgun Gothic, Microsoft YaHei UI, Segoe UI";
                case "ja": return "Yu Gothic UI, Microsoft YaHei UI, Segoe UI";
                default: return "Segoe UI";
            }
        }
    }

    /// <summary>A supported language: short code (persisted in settings) + native display name.</summary>
    public sealed record Language(string Code, string DisplayName)
    {
        /// <summary>
        /// Fallback for WPF bindings that lose <c>DisplayMemberPath</c> (the settings window
        /// previously showed <c>Language { Code = … }</c> when the path resolver returned null
        /// for record positional properties); ToString returns the native label either way.
        /// </summary>
        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// All supported languages, in UI order. The first (zh-CN) is the default/fallback.
    /// Add a new culture here and ship a <c>Strings.{code}.resx</c> to extend the set.
    /// </summary>
    public static readonly Language[] Supported =
    {
        new("zh-CN", "中文"),
        new("en", "English"),
        new("fr", "Français"),
        new("de", "Deutsch"),
        new("es", "Español"),
        new("ko", "한국어"),
    };

    /// <summary>Look up a localized string for the active culture; falls back to zh-CN, then the key.</summary>
    public static string Get(string key) => Strings.Get(key, _culture);

    /// <summary>Look up a localized string and apply <c>{0}</c>.. formatting (ML).</summary>
    public static string Format(string key, params object[] args) =>
        string.Format(Get(key), args);

    /// <summary>Set the language by code (e.g. "zh-CN" / "fr"). Unknown codes fall back to zh-CN.</summary>
    public static void SetLanguage(string code)
    {
        var match = Supported.FirstOrDefault(
            s => string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase));
        Culture = match is null ? new CultureInfo("zh-CN") : new CultureInfo(match.Code);
    }

    /// <summary>INotifyPropertyChanged bridge so static culture changes can drive WPF bindings.</summary>
    public sealed class Notifier : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public void RaiseChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Culture"));
        // Dummy readable property so bindings have a path.
        public string Culture => Localization.Get("Language");
    }
}

/// <summary>
/// Strong-typed accessor over the embedded Strings resx resources (ML).
/// ResourceManager reads the culture-specific .resources merged from Strings.resx and the
/// per-culture satellites. Missing keys fall back to zh-CN, then to the key itself.
/// </summary>
public static class Strings
{
    private static readonly ResourceManager _rm =
        new ResourceManager("Dsh.App.Resources.Strings", typeof(Strings).Assembly);

    private static readonly CultureInfo Fallback = new("zh-CN");

    public static string Get(string key, CultureInfo? culture = null)
    {
        // 1) Exact culture (or its parent, e.g. "fr").
        if (culture is not null)
        {
            var direct = _rm.GetString(key, culture);
            if (direct is not null) return direct;
        }
        // 2) zh-CN fallback (the default UI language) — never return a raw key when the
        //    base resource has a translation for the key.
        var baseValue = _rm.GetString(key, Fallback);
        return baseValue ?? key;
    }
}
