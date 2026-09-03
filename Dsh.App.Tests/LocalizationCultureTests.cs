using Dsh.App.Services;
using Xunit;

namespace Dsh.App.Tests;

/// <summary>
/// K3 regression tests: culture-aware formatting for the stats line. zh-CN uses 轮/步 and
/// " tokens"; en keeps turns/steps and " tok". Serialized with SessionStatsFormatterTests so
/// the shared Localization.Culture is not mutated in parallel.
/// </summary>
[Collection("Localization")]
public class LocalizationCultureTests
{
    [Fact]
    public void Format_uses_chinese_units_under_zh()
    {
        Localization.SetLanguage("zh-CN");
        var line = SessionStatsFormatter.Format(
            new Dsh.Contract.Projections.SessionStatsProjection
            {
                Turns = 3,
                Steps = 5,
                LlmMs = 1000,
                DecodeMs = 0,
                ToolMs = 0,
                TtftMs = 0,
                TtftSteps = 0,
                DecodeTokens = 0,
            },
            null);
        Assert.Contains("3轮", line);
        Assert.Contains("5步", line);
    }

    [Fact]
    public void Format_uses_english_units_under_en()
    {
        Localization.SetLanguage("en");
        var line = SessionStatsFormatter.Format(
            new Dsh.Contract.Projections.SessionStatsProjection
            {
                Turns = 3,
                Steps = 5,
                LlmMs = 1000,
                DecodeMs = 0,
                ToolMs = 0,
                TtftMs = 0,
                TtftSteps = 0,
                DecodeTokens = 0,
            },
            null);
        Assert.Contains("3 turns", line);
        Assert.Contains("5 steps", line);
    }

    [Fact]
    public void FormatTokens_respects_culture_unit()
    {
        Localization.SetLanguage("en");
        Assert.Contains(" tokens", SessionStatsFormatter.FormatTokens(1500));
        Localization.SetLanguage("zh-CN");
        Assert.Contains(" tokens", SessionStatsFormatter.FormatTokens(1500));
    }

    [Fact]
    public void FormatDuration_is_culture_stable()
    {
        // Duration is locale-neutral (m:ss / h:mm) — must not change under either culture.
        Localization.SetLanguage("zh-CN");
        var zh = SessionStatsFormatter.FormatDuration(90_000);
        Localization.SetLanguage("en");
        var en = SessionStatsFormatter.FormatDuration(90_000);
        Assert.Equal(zh, en);
    }
}
