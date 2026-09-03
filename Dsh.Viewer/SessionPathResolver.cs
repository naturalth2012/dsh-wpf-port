using System.IO;

namespace Dsh.Viewer;

/// <summary>
/// L1 disk-format decode: resolves a session file under <c>&lt;root&gt;/--&lt;encoded-cwd&gt;--/
/// &lt;encoded-sessionId&gt;/session.jsonl[.zstd]</c> and decodes path segments back to their
/// original cwd / sessionId (inverse of <c>encodeSegment</c>/<c>projectKey</c> in
/// <c>session-persistence-jsonl/src/format.ts</c>).
/// </summary>
public static class SessionPathResolver
{
    /// <summary>Directory prefix that wraps an encoded segment (dashes around it).</summary>
    private const string Seg = "--";

    /// <summary>Decode a filesystem-safe segment back to its original text. Encoded segments use
    /// <c>~XXXX</c> (hex) for characters that are unsafe in a file path; bare chars pass through.</summary>
    public static string DecodeSegment(string encoded)
    {
        if (string.IsNullOrEmpty(encoded)) return encoded;
        var sb = new System.Text.StringBuilder(encoded.Length);
        for (int i = 0; i < encoded.Length; i++)
        {
            char c = encoded[i];
            if (c == '~' && i + 4 < encoded.Length
                && Uri.IsHexDigit(encoded[i + 1]) && Uri.IsHexDigit(encoded[i + 2])
                && Uri.IsHexDigit(encoded[i + 3]) && Uri.IsHexDigit(encoded[i + 4]))
            {
                int code = (HexVal(encoded[i + 1]) << 12) | (HexVal(encoded[i + 2]) << 8)
                         | (HexVal(encoded[i + 3]) << 4) | HexVal(encoded[i + 4]);
                sb.Append((char)code);
                i += 4;
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static int HexVal(char c) => Uri.IsHexDigit(c)
        ? (c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10)
        : 0;

    /// <summary>
    /// Enumerate session folders under a root. Returns entries for every <c>--&lt;encoded-cwd&gt;--/
    /// &lt;encoded-sessionId&gt;/session.jsonl[.zstd]</c>. <paramref name="decode"/> receives the raw
    /// (still-encoded) segment path and returns the decoded workspace label for grouping.
    /// </summary>
    public static IEnumerable<SessionFileEntry> Scan(string root)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(root, "--*--"))
        {
            string wsSeg = Path.GetFileName(dir);
            string wsLabel = wsSeg.Length > 4 ? DecodeSegment(wsSeg[2..^2]) : wsSeg;
            foreach (var sessionDir in Directory.EnumerateDirectories(dir, "*"))
            {
                string sessionId = Path.GetFileName(sessionDir);
                string jsonl = Path.Combine(sessionDir, "session.jsonl");
                if (File.Exists(jsonl))
                {
                    yield return new SessionFileEntry(jsonl, wsLabel, sessionId, IsZstd: false);
                    continue;
                }
                string zstd = Path.Combine(sessionDir, "session.jsonl.zstd");
                if (File.Exists(zstd))
                {
                    yield return new SessionFileEntry(zstd, wsLabel, sessionId, IsZstd: true);
                }
            }
        }
    }
}

/// <summary>A discovered session file with its decoded workspace label.</summary>
public sealed record SessionFileEntry(string Path, string Workspace, string SessionId, bool IsZstd);
