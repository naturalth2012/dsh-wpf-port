using Dsh.App.Services;
using Xunit;

namespace Dsh.App.Tests;

[Collection("Localization")]
public class LocalizationTests
{
    [Fact]
    public void Get_ReturnsChinese_ForDefaultCulture()
    {
        Localization.SetLanguage("zh-CN");
        Assert.Equal("连接", Localization.Get("Connect"));
    }

    [Fact]
    public void Get_ReturnsEnglish_AfterSwitchingToEn()
    {
        Localization.SetLanguage("en");
        Assert.Equal("Connect", Localization.Get("Connect"));
    }

    [Fact]
    public void Get_FallsBackToKey_ForUnknownKey()
    {
        Localization.SetLanguage("zh-CN");
        Assert.Equal("NoSuchKey", Localization.Get("NoSuchKey"));
    }

    [Fact]
    public void SetLanguage_TogglesBetweenZhAndEn()
    {
        Localization.SetLanguage("en");
        Assert.Equal("New Session", Localization.Get("NewSession"));
        Localization.SetLanguage("zh-CN");
        Assert.Equal("新建会话", Localization.Get("NewSession"));
    }

    /// <summary>
    /// Regression: the language dropdown was showing "Language { Code = zh-CN }" instead of the
    /// native label. The fix overrides ToString so WPF's DisplayMemberPath fallback also renders
    /// the human-readable name (and the record's positional DisplayName property is still used
    /// for proper binding).
    /// </summary>
    [Fact]
    public void Language_RecordToString_ReturnsDisplayName()
    {
        var zh = Localization.Supported.First(s => s.Code == "zh-CN");
        Assert.Equal("中文", zh.ToString());
        Assert.Equal("中文", zh.DisplayName);
        var en = Localization.Supported.First(s => s.Code == "en");
        Assert.Equal("English", en.ToString());
    }
}
