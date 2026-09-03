using System.Collections.Generic;
using System.Globalization;

namespace Dsh.App;

/// <summary>
/// Splits a search snippet into highlighted / plain segments so the UI can render the
/// matched query substring(s) with emphasis. The host's <c>session.search</c> returns a
/// plain <c>snippet</c> string without match offsets (see <c>SessionSearchItem</c>), so
/// highlighting must be reconstructed client-side (P2-13). Matching is case-insensitive
/// and culture-aware via <see cref="StringComparison.CurrentCultureIgnoreCase"/>.
/// </summary>
public static class SnippetHighlighter
{
    /// <summary>
    /// One piece of a split snippet: <see cref="Text"/> plus whether it matches the query.
    /// </summary>
    public sealed record Segment(string Text, bool IsMatch);

    /// <summary>
    /// Split <paramref name="snippet"/> around every (case-insensitive) occurrence of
    /// <paramref name="query"/>. Returns a single non-matching segment when the query is
    /// blank or absent; never returns null segments. Overlapping/adjacent matches are merged.
    /// </summary>
    public static IReadOnlyList<Segment> Highlight(string? snippet, string? query)
    {
        var result = new List<Segment>();
        if (string.IsNullOrWhiteSpace(snippet))
            return result;

        var text = snippet!;
        if (string.IsNullOrWhiteSpace(query))
        {
            result.Add(new Segment(text, false));
            return result;
        }

        var q = query!.Trim();
        if (q.Length == 0)
        {
            result.Add(new Segment(text, false));
            return result;
        }

        var matches = new List<(int Start, int End)>();
        var idx = 0;
        while ((idx = CultureInfo.CurrentCulture.CompareInfo.IndexOf(text, q, idx, CompareOptions.IgnoreCase)) >= 0)
        {
            matches.Add((idx, idx + q.Length));
            idx += q.Length;
        }

        if (matches.Count == 0)
        {
            result.Add(new Segment(text, false));
            return result;
        }

        // Merge adjacent/overlapping matches.
        matches.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(int Start, int End)> { matches[0] };
        foreach (var m in matches.Skip(1))
        {
            var last = merged[^1];
            if (m.Start <= last.End)
                merged[^1] = (last.Start, Math.Max(last.End, m.End));
            else
                merged.Add(m);
        }

        var cursor = 0;
        foreach (var (start, end) in merged)
        {
            if (start > cursor)
                result.Add(new Segment(text[cursor..start], false));
            result.Add(new Segment(text[start..end], true));
            cursor = end;
        }
        if (cursor < text.Length)
            result.Add(new Segment(text[cursor..], false));

        return result;
    }
}
