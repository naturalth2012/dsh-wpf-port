namespace Dsh.App;

/// <summary>
/// Helpers shared by the Markdown rendering pipeline.
/// </summary>
public static class MarkdownSegmenter
{
    /// <summary>
    /// A loose "looks like a filesystem path" heuristic for H4 file mentions: contains a
    /// slash/dot or starts with a drive/root. Avoids treating arbitrary inline code (e.g. a
    /// variable name) as a clickable file. Public so the Markdown renderer can reuse it (M0).
    /// </summary>
    public static bool IsFilePath(string candidate)
    {
        if (candidate.Length >= 2 && (candidate[1] == ':' || candidate[1] == '\\')) return true; // C:\ or c:\
        if (candidate.StartsWith('/')) return true;                       // /abs/path
        if (candidate.StartsWith('.')) return true;                       // ./x or ../x
        return candidate.Contains('/') || candidate.Contains('\\') || candidate.Contains('.');
    }
}
