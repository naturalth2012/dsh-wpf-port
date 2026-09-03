using Dsh.App.Services;
using Xunit;

namespace Dsh.App.Tests;

/// <summary>
/// ML regression tests: each supported culture resolves the right translation, unknown keys
/// fall back to zh-CN (the default UI language), and SetLanguage maps codes to cultures.
/// </summary>
[Collection("Localization")]
public class MultiLanguageTests
{
    [Theory]
    [InlineData("zh-CN", "新建会话")]
    [InlineData("en", "New Session")]
    [InlineData("fr", "Nouvelle session")]
    [InlineData("de", "Neue Sitzung")]
    [InlineData("es", "Nueva sesión")]
    [InlineData("ko", "새 세션")]
    public void NewSession_resolves_per_culture(string code, string expected)
    {
        Localization.SetLanguage(code);
        Assert.Equal(expected, Localization.Get("NewSession"));
    }

    [Fact]
    public void Truly_missing_key_returns_key()
    {
        Localization.SetLanguage("en");
        Assert.Equal("NoSuchKey123", Localization.Get("NoSuchKey123"));
    }

    [Fact]
    public void Unknown_language_code_falls_back_to_chinese()
    {
        Localization.SetLanguage("xx");
        Assert.Equal("zh-CN", Localization.Culture.Name);
        Assert.Equal("连接", Localization.Get("Connect"));
    }

    [Fact]
    public void All_supported_codes_are_available()
    {
        var codes = Localization.Supported.Select(s => s.Code).ToArray();
        Assert.Contains("zh-CN", codes);
        Assert.Contains("en", codes);
        Assert.Contains("fr", codes);
        Assert.Contains("de", codes);
        Assert.Contains("es", codes);
        Assert.Contains("ko", codes);
    }
}
