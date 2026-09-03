using Dsh.App;
using Dsh.App.Services;
using Dsh.Contract.Projections;
using Xunit;

namespace Dsh.App.Tests;

/// <summary>
/// Tests for <see cref="SessionStatsFormatter"/> (P2-7): compact session-stats / token-usage
/// status-line formatting. Pinned to English so assertions are stable regardless of the host
/// UI culture; serialized with the K3 culture tests via the shared collection.
/// </summary>
[Collection("Localization")]
public class SessionStatsFormatterTests
{
    [Fact]
    public void Null_projections_yield_empty()
    {
        Localization.SetLanguage("en");
        Assert.Equal("", SessionStatsFormatter.Format(null, null));
    }

    [Fact]
    public void Stats_projection_formats_turns_steps_duration()
    {
        Localization.SetLanguage("en");

        var stats = new SessionStatsProjection
        {
            Turns = 4,
            Steps = 21,
            LlmMs = 30_000,
            DecodeMs = 20_000,
            ToolMs = 10_000,
            TtftMs = 0,
            TtftSteps = 0,
            DecodeTokens = 0,
        };
        var line = SessionStatsFormatter.Format(stats, null);
        // FormatDuration is now Web-style (no leading zero): 60s -> 1m0s.
        Assert.Equal("4 turns · 21 steps · 1m0s", line);
    }

    [Fact]
    public void Duration_formats_web_style()
    {
        // Web StatsLine formatDuration: <60s uses x.xs, >=60s uses xm Ys (no hour bucket, no padding).
        Assert.Equal("35s", SessionStatsFormatter.FormatDuration(35_000));
        Assert.Equal("65m0s", SessionStatsFormatter.FormatDuration(3_900_000));
        Assert.Equal("2m5s", SessionStatsFormatter.FormatDuration(125_000));
        Assert.Equal("2m42s", SessionStatsFormatter.FormatDuration(162_000));
    }

    [Theory]
    [InlineData(500, "500 tokens")]
    [InlineData(1_200, "1.2k tokens")]
    [InlineData(1_234_000, "1.2M tokens")]
    public void Token_count_formats_compactly(long n, string expected)
    {
        Localization.SetLanguage("en");
        Assert.Equal(expected, SessionStatsFormatter.FormatTokens(n));
    }

    [Fact]
    public void Token_usage_summed_across_all_channels()
    {
        Localization.SetLanguage("en");
        var tokens = new TokenUsageProjection
        {
            OutputTokens = 100,
            UncachedInputTokens = 200,
            CacheReadTokens = 300,
            CacheWriteTokens = 400,
        };
        // 1000 tokens → "1.0k tokens".
        Assert.EndsWith("1.0k tokens", SessionStatsFormatter.Format(null, tokens));
    }

    [Fact]
    public void Rich_without_pressure_has_no_ring_fraction()
    {
        Localization.SetLanguage("en");
        var stats = new SessionStatsProjection
        {
            Turns = 2, Steps = 5, LlmMs = 20_000, DecodeMs = 10_000, ToolMs = 5_000,
            TtftMs = 900, TtftSteps = 1, DecodeTokens = 1000,
        };
        var (fraction, detail) = SessionStatsFormatter.FormatRich(stats, null, null);
        Assert.Null(fraction);
        // Web-aligned groups: counts | durations | speeds (each field present).
        Assert.Contains("2 turns · 5 steps", detail);   // counts
        Assert.Contains("LLM 20s", detail);             // LLM duration (llmMs=20_000)
        Assert.Contains("Tool call 5s", detail);        // tool-call duration (toolMs=5_000)
        Assert.Contains("TTFT avg 0.9s", detail);       // ttft average (900/1 = 900ms)
        Assert.Contains("100 tok/s", detail);           // decode rate (1000 tok / 10s)
    }

    [Fact]
    public void Rich_pressure_computes_ring_fraction()
    {
        Localization.SetLanguage("en");
        var pressure = new ContextPressureProjection { PressureTokens = 40_000, ContextWindow = 100_000 };
        var (fraction, _) = SessionStatsFormatter.FormatRich(null, null, pressure);
        Assert.Equal(0.4, fraction!.Value, precision: 2);
    }

    [Fact]
    public void Rich_pressure_fraction_is_clamped_to_1()
    {
        var pressure = new ContextPressureProjection { PressureTokens = 150_000, ContextWindow = 100_000 };
        var (fraction, _) = SessionStatsFormatter.FormatRich(null, null, pressure);
        Assert.Equal(1.0, fraction!.Value);
    }

    [Fact]
    public void Rich_null_projections_yield_empty_detail()
    {
        var (fraction, detail) = SessionStatsFormatter.FormatRich(null, null, null);
        Assert.Null(fraction);
        Assert.Equal("", detail);
    }
}
