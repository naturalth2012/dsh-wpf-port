using System.IO;
using Dsh.Viewer;

namespace Dsh.Viewer.Tests;

/// <summary>
/// L1 disk-path regression tests (os/03): <c>DecodeSegment</c> must be the faithful inverse of the
/// upstream <c>encodeSegment</c> — turning the filesystem-safe <c>~XXXX</c> hex escapes back into
/// their original characters, with <c>.</c>/<c>..</c> and bare chars passing through untouched.
/// <c>Scan</c> must discover both <c>session.jsonl</c> and <c>session.jsonl.zstd</c> under the
/// <c>--&lt;encoded-cwd&gt;--/&lt;encoded-sessionId&gt;/</c> layout, grouping by the decoded cwd.
/// </summary>
public class SessionPathResolverTests
{
    // ── DecodeSegment ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("D-Archive-~5DE5~4F5C", "D-Archive-工作")]
    [InlineData("D-github", "D-github")]
    [InlineData("", "")]
    [InlineData("plain-ascii-123", "plain-ascii-123")]
    public void DecodeSegment_unescapes_tilde_hex(string encoded, string expected)
    {
        Assert.Equal(expected, SessionPathResolver.DecodeSegment(encoded));
    }

    [Fact]
    public void DecodeSegment_handles_multiple_consecutive_escapes()
    {
        // ~XXXX encodes a single UTF-16 code unit; consecutive escapes decode consecutive chars.
        Assert.Equal("工作", SessionPathResolver.DecodeSegment("~5DE5~4F5C"));
    }

    [Fact]
    public void DecodeSegment_leaves_dots_and_slashes_alone()
    {
        // '.' and '/' are not hex-escaped by encodeSegment (they are safe on the wire),
        // so DecodeSegment must pass them through without treating them specially.
        Assert.Equal("..", SessionPathResolver.DecodeSegment(".."));
        Assert.Equal("a.b/c", SessionPathResolver.DecodeSegment("a.b/c"));
    }

    [Fact]
    public void DecodeSegment_treats_lone_tilde_not_followed_by_4_hex_as_literal()
    {
        Assert.Equal("~", SessionPathResolver.DecodeSegment("~"));
        Assert.Equal("~AB", SessionPathResolver.DecodeSegment("~AB"));      // 2 hex then end
        Assert.Equal("a~ZZZZb", SessionPathResolver.DecodeSegment("a~ZZZZb")); // Z not hex
    }

    [Theory]
    // Each ~XXXX decodes to a single ASCII char (UTF-16 code unit 0x00NN).
    // The hex must use the full 4-digit ASCII BMP range; do not reuse the Chinese examples from
    // the "creates tilde-hex escapes" tests above (those exercise the encoder, not the decoder).
    [InlineData("~004C~006F~0063~0061~006C", "Local")]
    [InlineData("~004F~0053", "OS")]
    [InlineData("~0041~0042~0043", "ABC")]
    public void DecodeSegment_matches_observed_real_encodings(string hex, string expected)
    {
        // Values observed in the live ~/.dsh/sessions directory segment names.
        Assert.Equal(expected, SessionPathResolver.DecodeSegment(hex));
    }

    // ── Scan ──────────────────────────────────────────────────────────────────────────────────

    private static string MakeTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "dshviewer-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void Scan_returns_empty_for_missing_root()
    {
        Assert.Empty(SessionPathResolver.Scan(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid())));
    }

    [Fact]
    public void Scan_discovers_plain_and_zstd_sessions_and_decodes_workspace()
    {
        string root = MakeTempRoot();
        try
        {
            // workspace segment (encoded cwd), with a decoded label containing a non-ASCII char.
            string wsDir = Path.Combine(root, "--D-Archive-~5DE5~4F5C--");
            string s1Dir = Path.Combine(wsDir, "session-aaa");
            string s2Dir = Path.Combine(wsDir, "session-bbb");
            Directory.CreateDirectory(s1Dir);
            Directory.CreateDirectory(s2Dir);
            File.WriteAllText(Path.Combine(s1Dir, "session.jsonl"), "{}\n");
            File.WriteAllText(Path.Combine(s2Dir, "session.jsonl.zstd"), "x"); // content irrelevant to scan

            var entries = SessionPathResolver.Scan(root).ToList();

            Assert.Equal(2, entries.Count);
            Assert.All(entries, e => Assert.Equal("D-Archive-工作", e.Workspace));
            var plain = entries.Single(e => e.Path.EndsWith("session.jsonl"));
            Assert.False(plain.IsZstd);
            Assert.Equal("session-aaa", plain.SessionId);
            var zstd = entries.Single(e => e.Path.EndsWith(".zstd"));
            Assert.True(zstd.IsZstd);
            Assert.Equal("session-bbb", zstd.SessionId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_ignores_non_session_layout_dirs()
    {
        string root = MakeTempRoot();
        try
        {
            // A stray dir that does not follow the --<seg>--/<id>/session.jsonl layout.
            Directory.CreateDirectory(Path.Combine(root, "not-encoded"));
            Directory.CreateDirectory(Path.Combine(root, "--ws--", "missing-file-dir"));
            Assert.Empty(SessionPathResolver.Scan(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_groups_multiple_workspaces_by_decoded_label()
    {
        string root = MakeTempRoot();
        try
        {
            foreach (var (seg, label) in new[] { ("--D-github--", "D-github"), ("--D-Archive-~5DE5~4F5C--", "D-Archive-工作") })
            {
                string wsDir = Path.Combine(root, seg);
                string sDir = Path.Combine(wsDir, "session-x");
                Directory.CreateDirectory(sDir);
                File.WriteAllText(Path.Combine(sDir, "session.jsonl"), "{}\n");
            }
            var entries = SessionPathResolver.Scan(root).ToList();
            Assert.Equal(new[] { "D-Archive-工作", "D-github" },
                entries.Select(e => e.Workspace).OrderBy(x => x));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
