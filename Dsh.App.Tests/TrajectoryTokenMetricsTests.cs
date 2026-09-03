using System.Linq;
using Xunit;

namespace Dsh.App.Tests;

/// <summary>
/// Tests for the per-step token metrics extracted from assistant payloads
/// (native parity: trajectory-record.ts input / cacheRead / cacheWrite / output / think, plus
/// TTFT derived from assistantMetrics as the native product derives it).
///
/// Hosts differ in where they put these counts, so the readers try the nested <c>usage</c> object
/// first and then flat/aliased names. These tests pin that flexibility, and — importantly — pin
/// that a payload WITHOUT the numbers yields null rather than a fabricated 0.
/// </summary>
public sealed class TrajectoryTokenMetricsTests
{
    private static SessionFold Fold(params string[] events)
    {
        var fold = new SessionFold();
        foreach (var json in events)
        {
            fold.Fold(System.Text.Json.JsonDocument.Parse(json).RootElement);
        }
        return fold;
    }

    private static SessionFold.TrajectoryStep SingleStep(SessionFold fold)
        => Assert.Single(fold.Trajectory);

    [Fact]
    public void Tokens_are_read_from_a_nested_usage_object()
    {
        var fold = Fold(
            """{"type":"assistant/message","seq":1,"data":{"usage":{"input":100,"cacheRead":20,"cacheWrite":30,"output":40,"think":50}}}""");

        var step = SingleStep(fold);
        Assert.Equal(100, step.InputTokens);
        Assert.Equal(20, step.CacheReadTokens);
        Assert.Equal(30, step.CacheWriteTokens);
        Assert.Equal(40, step.OutputTokens);
        Assert.Equal(50, step.ThinkTokens);
    }

    [Fact]
    public void Tokens_are_read_from_flat_fields()
    {
        var fold = Fold(
            """{"type":"assistant/message","seq":1,"data":{"input":7,"cacheRead":8,"cacheWrite":9,"output":10,"think":11}}""");

        var step = SingleStep(fold);
        Assert.Equal(7, step.InputTokens);
        Assert.Equal(8, step.CacheReadTokens);
        Assert.Equal(9, step.CacheWriteTokens);
        Assert.Equal(10, step.OutputTokens);
        Assert.Equal(11, step.ThinkTokens);
    }

    [Fact]
    public void Provider_style_aliases_are_recognised()
    {
        // Anthropic-style nested naming.
        var fold = Fold(
            """{"type":"assistant/message","seq":1,"data":{"usage":{"input_tokens":111,"cache_read_input_tokens":222,"cache_creation_input_tokens":333}}}""");

        var step = SingleStep(fold);
        Assert.Equal(111, step.InputTokens);
        Assert.Equal(222, step.CacheReadTokens);
        Assert.Equal(333, step.CacheWriteTokens);
    }

    [Fact]
    public void Missing_token_fields_stay_null_rather_than_zero()
    {
        // A payload without usage must not fabricate counts — the UI shows the token block only
        // when at least one real number is present.
        var fold = Fold(
            """{"type":"assistant/message","seq":1,"data":{"message":{"content":[{"type":"text","text":"hi"}]}}}""");

        var step = SingleStep(fold);
        Assert.Null(step.InputTokens);
        Assert.Null(step.CacheReadTokens);
        Assert.Null(step.CacheWriteTokens);
        Assert.Null(step.OutputTokens);
        Assert.Null(step.ThinkTokens);
        Assert.Null(step.TtftMs);
    }

    [Fact]
    public void Ttft_is_derived_from_assistant_metrics()
    {
        // Native derivation: firstTokenTime - stepStartTime.
        var fold = Fold(
            """{"type":"assistant/message","seq":1,"data":{"assistantMetrics":{"stepStartTime":1000,"firstTokenTime":1500,"outputTokens":200}}}""");

        var step = SingleStep(fold);
        Assert.Equal(500, step.TtftMs);
        Assert.Equal(200, step.OutputTokens);
    }

    [Fact]
    public void Ttft_is_null_without_both_timestamps()
    {
        // Only one of the two times present ⇒ cannot derive, so report null (never guess).
        var fold = Fold(
            """{"type":"assistant/message","seq":1,"data":{"assistantMetrics":{"stepStartTime":1000}}}""");

        Assert.Null(SingleStep(fold).TtftMs);
    }

    [Fact]
    public void Ttft_is_null_when_not_monotonic()
    {
        // firstTokenTime before stepStartTime is nonsense; report null rather than a negative.
        var fold = Fold(
            """{"type":"assistant/message","seq":1,"data":{"assistantMetrics":{"stepStartTime":2000,"firstTokenTime":1000}}}""");

        Assert.Null(SingleStep(fold).TtftMs);
    }

    [Fact]
    public void Token_reading_does_not_disturb_text_or_kind()
    {
        var fold = Fold(
            """{"type":"assistant/message","seq":1,"data":{"usage":{"output":3},"message":{"content":[{"type":"text","text":"answer"}]}}}""");

        var step = SingleStep(fold);
        Assert.Equal("message", step.Kind);
        Assert.Equal("answer", step.Text);
        Assert.Equal(3, step.OutputTokens);
    }
}
