using Dsh.App.Services;
using Dsh.Contract.Projections;

namespace Dsh.App;

/// <summary>
/// Formats session-stats / token-usage projections (P2-7) into a compact status line. Pure text
/// logic in <c>Dsh.App</c> so it is unit-testable; the view model calls it when the selected
/// session has a projection. Returns an empty string when neither projection is present.
/// </summary>
public static class SessionStatsFormatter
{
    /// <summary>Format one line from the projections; empty when both are null.</summary>
    public static string Format(SessionStatsProjection? stats, TokenUsageProjection? tokens)
    {
        if (stats is null && tokens is null) return "";

        // K3: unit labels follow the active UI culture via localized resource keys.
        var turnsLabel = Localization.Get("Stats.TurnsUnit");
        var stepsLabel = Localization.Get("Stats.StepsUnit");

        var parts = new List<string>();
        if (stats is not null)
        {
            parts.Add($"{stats.Turns}{turnsLabel}");
            parts.Add($"{stats.Steps}{stepsLabel}");
            long llmMs = stats.LlmMs + stats.DecodeMs + stats.ToolMs;
            parts.Add(FormatDuration(llmMs));
        }
        if (tokens is not null)
        {
            parts.Add(FormatTokens(tokens.OutputTokens + tokens.UncachedInputTokens + tokens.CacheReadTokens + tokens.CacheWriteTokens));
        }
        return string.Join(" · ", parts);
    }

    /// <summary>Format milliseconds the same way the Web StatsLine does: "45.2s" under a minute,
    /// "2m42s" from there on (compact, no leading zeros).</summary>
    public static string FormatDuration(long ms)
    {
        double s = ms / 1_000.0;
        if (s < 60) return $"{Math.Round(s * 10) / 10:0.#}s";
        long whole = (long)Math.Round(s);
        return $"{whole / 60}m{whole % 60}s";
    }

    /// <summary>Compact token count with a culture-aware unit label (legacy line form).</summary>
    public static string FormatTokens(long n)
    {
        var unit = Localization.Get("Stats.TokensUnit");
        if (n >= 1_000_000) return $"{n / 1_000_000.0:F1}M{unit}";
        if (n >= 1_000) return $"{n / 1000.0:F1}k{unit}";
        return $"{n}{unit}";
    }

    /// <summary>Compact token count mirroring Web formatTokens: 517 / 12.2K / 1.2M (one decimal
    /// only below three digits). No trailing unit — the unit rides the calling i18n key.</summary>
    public static string FormatTokenCount(long n)
    {
        if (n < 1_000) return n.ToString();
        double scaled = n / 1_000.0;
        if (n >= 1_000_000) return $"{Math.Round(scaled / 1000.0 * 10) / 10}M";
        if (scaled >= 100) return $"{Math.Round(scaled)}K";
        return $"{Math.Round(scaled * 10) / 10}K";
    }

    /// <summary>Sum of the three disjoint prompt-side billing buckets (mirrors billedInputTokens).</summary>
    public static long BilledInputTokens(TokenUsageProjection usage) =>
        usage.UncachedInputTokens + usage.CacheReadTokens + usage.CacheWriteTokens;

    /// <summary>
    /// Display-ready cache-hit share of prompt-side input (mirrors cacheHitPercent in the Web
    /// StatsLine: integer rounding that stays below 100; a full hit returns "100"; no billed
    /// input returns null).
    /// </summary>
    public static string? CacheHitPercent(TokenUsageProjection usage)
    {
        long denominator = BilledInputTokens(usage);
        if (denominator == 0) return null;
        long missed = usage.UncachedInputTokens + usage.CacheWriteTokens;
        if (missed == 0) return "100";

        // Integer percent with exact small-factor rounding (avoids float drift at the 100 edge).
        long denominatorQ = denominator / 200;
        long denominatorR = denominator % 200;
        long lower = 0, upper = 100;
        while (lower < upper)
        {
            long candidate = (lower + upper + 1) / 2;
            long factor = candidate * 2 - 1;
            long threshold = factor * denominatorQ + (factor * denominatorR + 199) / 200;
            if (usage.CacheReadTokens >= threshold) lower = candidate;
            else upper = candidate - 1;
        }
        return lower.ToString();
    }

    /// <summary>
    /// C17 rich stats line aligned to the Web <c>StatsLine.tsx</c>: pipe-separated groups
    /// (counts | durations | speeds | cacheHit | tokens); a group with no data drops out whole.
    /// Also returns the context-ring fraction (projected/pressure tokens over the window).
    /// </summary>
    /// <returns>Ring fraction (0..1, null when unknown) and the display line (may be empty).</returns>
    public static (double? RingFraction, string Detail) FormatRich(
        SessionStatsProjection? stats,
        TokenUsageProjection? tokens,
        ContextPressureProjection? pressure)
    {
        // Ring: prefer projectedTokens (provider sample carried forward), fall back to pressureTokens.
        double? fraction = null;
        long? used = pressure?.ProjectedTokens ?? pressure?.PressureTokens;
        if (used is > 0 && pressure?.ContextWindow is > 0)
            fraction = Math.Clamp((double)used.Value / pressure.ContextWindow.Value, 0.0, 1.0);

        var groups = new List<string>();

        // counts group (only when there were steps).
        if (stats is not null && stats.Steps > 0)
        {
            groups.Add(Fmt(Localization.Get("Stats.Counts"),
                "turns", stats.Turns.ToString(), "steps", stats.Steps.ToString()));

            // durations group: LLM and tool-call times are reported separately (not summed).
            var durations = new List<string>();
            if (stats.LlmMs > 0)
                durations.Add(Fmt(Localization.Get("Stats.Llm"), "duration", FormatDuration(stats.LlmMs)));
            if (stats.ToolMs > 0)
                durations.Add(Fmt(Localization.Get("Stats.ToolCall"), "duration", FormatDuration(stats.ToolMs)));
            if (durations.Count > 0) groups.Add(string.Join(" · ", durations));

            // speeds group: TTFT average + decode throughput.
            var speeds = new List<string>();
            if (stats.TtftSteps > 0)
                speeds.Add(Fmt(Localization.Get("Stats.TtftAverage"), "duration", FormatDuration(stats.TtftMs / stats.TtftSteps)));
            if (stats.DecodeMs > 0 && stats.DecodeTokens > 0)
            {
                double throughput = stats.DecodeTokens / (stats.DecodeMs / 1000.0);
                speeds.Add(Fmt(Localization.Get("Stats.TokensPerSecond"),
                    "throughput", (Math.Round(throughput * 10) / 10).ToString("0.#")));
            }
            if (speeds.Count > 0) groups.Add(string.Join(" · ", speeds));
        }

        // billing group (cache hit + in/out tokens), gated on actual token activity.
        if (tokens is not null && (BilledInputTokens(tokens) > 0 || tokens.OutputTokens > 0))
        {
            var hit = CacheHitPercent(tokens);
            if (hit is not null)
                groups.Add(Fmt(Localization.Get("Stats.CacheHit"), "percent", hit));
            groups.Add(Fmt(Localization.Get("Stats.Tokens"),
                "input", FormatTokenCount(BilledInputTokens(tokens)),
                "output", FormatTokenCount(tokens.OutputTokens)));
        }

        return (fraction, string.Join(" | ", groups));
    }

    /// <summary>Fill a "{name}" placeholder template (case-insensitive), matching Web i18n shape.</summary>
    private static string Fmt(string template, params string[] pairs)
    {
        string s = template;
        for (int i = 0; i + 1 < pairs.Length; i += 2)
            s = s.Replace("{" + pairs[i] + "}", pairs[i + 1], StringComparison.OrdinalIgnoreCase);
        return s;
    }
}
