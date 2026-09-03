using System;
using System.Collections.Generic;

namespace Dsh.App;

/// <summary>Which kind of line a <see cref="TrajectoryRow"/> represents in the ledger.</summary>
public enum TrajectoryRowKind
{
    /// <summary>"Turn N" group header (native grouping level).</summary>
    TurnHeader,

    /// <summary>"Request #N" group header (assistant request within a turn).</summary>
    RequestHeader,

    /// <summary>An actual trajectory step (user / assistant / tool / context …).</summary>
    Step,
}

/// <summary>
/// One display line of the trajectory ledger. The native product renders its ledger as a
/// flattened, virtualized list of group headers and steps, so the WPF view flattens the
/// trajectory the same way: a flat list keeps ListBox virtualization effective (a nested
/// TreeView or grouped ItemsControl would realize far more containers).
/// </summary>
public sealed class TrajectoryRow
{
    public TrajectoryRowKind RowKind { get; init; }

    /// <summary>Left-hand label: "Turn 1" / "Request #1" / the upper-cased step kind.</summary>
    public string Label { get; init; } = "";

    /// <summary>Right-hand / secondary text (single-line, ellipsized by the view).</summary>
    public string Summary { get; init; } = "";

    /// <summary>Trailing meta text such as a formatted duration.</summary>
    public string? Meta { get; init; }

    /// <summary>Indentation level (0 = turn header, 1 = request header, 2 = step).</summary>
    public int Indent { get; init; }

    /// <summary>True when the step is a failure (rendered with the error brush).</summary>
    public bool IsError { get; init; }

    /// <summary>The underlying step; null for group headers.</summary>
    public SessionFold.TrajectoryStep? Step { get; init; }
}

/// <summary>
/// Builds the flattened ledger rows from a trajectory. Kept as a pure static function (no WPF
/// dependencies beyond the row type) so the grouping / folding / filtering rules stay unit
/// testable without spinning up a dispatcher.
/// </summary>
public static class TrajectoryLedger
{
    /// <summary>
    /// Flatten <paramref name="steps"/> into display rows, emitting Turn and Request group headers
    /// on change (native parity: "Turn N" → "Request #N" → steps).
    /// </summary>
    /// <param name="collapseTurns">When true only Turn headers are emitted (content hidden).</param>
    /// <param name="collapseCalls">When true tool steps are hidden (native "Collapse calls").</param>
    /// <param name="search">
    /// Case-insensitive filter. While non-empty, turn folding is ignored so matches stay visible.
    /// </param>
    public static List<TrajectoryRow> Build(
        IReadOnlyList<SessionFold.TrajectoryStep> steps,
        bool collapseTurns,
        bool collapseCalls,
        string? search)
    {
        var rows = new List<TrajectoryRow>(steps.Count + 8);
        bool filtering = !string.IsNullOrWhiteSpace(search);
        // Ordinal ignore-case comparison is allocation-free and culture-independent — important
        // because this runs for every step on every rebuild (and rebuilds happen per keystroke
        // while the user types in the search box).
        var needle = filtering ? search!.Trim() : null;

        int currentTurn = int.MinValue;
        int currentRequest = int.MinValue;

        foreach (var step in steps)
        {
            if (filtering && !Matches(step, needle!)) continue;

            // Turn header on change.
            if (step.TurnIndex != currentTurn)
            {
                currentTurn = step.TurnIndex ?? 0;
                // A new turn invalidates the request counter so the next request re-emits its header.
                currentRequest = int.MinValue;
                rows.Add(new TrajectoryRow
                {
                    RowKind = TrajectoryRowKind.TurnHeader,
                    Label = "Turn " + currentTurn,
                    Indent = 0,
                });
            }

            // When turns are collapsed we stop after the header, unless the user is filtering
            // (hiding matched rows behind a collapsed group would defeat the search).
            if (collapseTurns && !filtering) continue;

            // Request header on change (only steps that belong to a request emit one).
            if (step.RequestIndex is { } req && req != currentRequest)
            {
                currentRequest = req;
                rows.Add(new TrajectoryRow
                {
                    RowKind = TrajectoryRowKind.RequestHeader,
                    Label = "Request #" + req,
                    Indent = 1,
                });
            }

            if (collapseCalls && step.Kind == "tool") continue;

            rows.Add(new TrajectoryRow
            {
                RowKind = TrajectoryRowKind.Step,
                Label = KindLabel(step.Kind),
                Summary = SingleLine(step.Text),
                Meta = FormatDuration(step.DurationMs),
                Indent = 2,
                IsError = step.IsError,
                Step = step,
            });
        }

        return rows;
    }

    /// <summary>Upper-cased native label for a step kind (the native ledger upper-cases these).</summary>
    public static string KindLabel(string kind) => kind switch
    {
        "message" => "ASSISTANT",
        "subtool" => "SUBTOOL",
        _ => kind.ToUpperInvariant(),
    };

    /// <summary>Culture-independent duration text: "820ms" under a second, else "3.4s".</summary>
    public static string FormatDuration(long? ms)
    {
        if (ms is null) return "";
        if (ms < 1000) return ms + "ms";
        return (ms.Value / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s";
    }

    /// <summary>
    /// Public counterpart of the private <see cref="SingleLine"/>: collapse text to a single
    /// whitespace-squashed line. Exposed so the WPF shell can build one-line window titles from
    /// multi-line message bodies without duplicating the logic.
    /// </summary>
    public static string ToSingleLine(string? text) => SingleLine(text);

    /// <summary>
    /// Truncate to <paramref name="max"/> chars with an ellipsis. Exposed alongside
    /// <see cref="ToSingleLine"/> for the same reason (title/summary building in the WPF shell).
    /// </summary>
    public static string TruncateTo(string? s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

    /// <summary>Collapse the summary to a single line so row heights stay uniform (a variable
    /// row height would defeat the fixed-height virtualization this list relies on).</summary>
    private static string SingleLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text.Replace("\r", " ").Replace("\n", " ");
        // Squash runs of whitespace so a multi-line payload doesn't produce a long blank run.
        var chars = new List<char>(s.Length);
        bool lastWasSpace = false;
        foreach (var c in s)
        {
            bool isSpace = c == ' ' || c == '\t';
            if (isSpace && lastWasSpace) continue;
            chars.Add(isSpace ? ' ' : c);
            lastWasSpace = isSpace;
        }
        return new string(chars.ToArray()).Trim();
    }

    /// <summary>Case-insensitive match across the fields a user would plausibly search for.</summary>
    private static bool Matches(SessionFold.TrajectoryStep step, string needle)
    {
        if (Contains(step.Text, needle)) return true;
        if (Contains(step.Kind, needle)) return true;
        if (Contains(step.CallId, needle)) return true;
        return false;
    }

    private static bool Contains(string? haystack, string needle)
        => haystack is not null
           && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}
