namespace Dsh.Wpf.Controls;

/// <summary>
/// Resolve a possibly-relative file path to an absolute one using known cwds.
/// The host's <c>openPath</c> uses the harness cwd, but the AI sometimes returns
/// paths relative to whatever subdirectory the tool ran in (e.g. <c>today_date.txt</c>).
/// Try the current session's cwd first (most accurate), then the harness root,
/// then the WPF process cwd. If none match, leave the path as-is so the caller can
/// surface the underlying error instead of guessing.
/// </summary>
internal static class FilePathResolver
{
    public static string Resolve(string? path, string? sessionCwd, string? harnessRoot)
    {
        if (string.IsNullOrWhiteSpace(path)) return path ?? string.Empty;
        if (System.IO.Path.IsPathRooted(path)) return path;

        // Order matters: session cwd is the most likely match (the AI's tool cwd),
        // harness root is the next most common, then process cwd.
        var candidates = new[]
        {
            sessionCwd,
            harnessRoot,
            Environment.CurrentDirectory,
        };

        foreach (var root in candidates)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            try
            {
                var combined = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path));
                if (System.IO.File.Exists(combined)) return combined;
            }
            catch
            {
                // Bad path chars / unreachable volume — skip this candidate.
            }
        }
        return path;
    }
}
