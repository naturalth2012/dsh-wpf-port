using System.IO;
using System.Text.Json;
using Dsh.App;
using Dsh.App.Services;
using Dsh.Viewer;

namespace Dsh.Viewer.Tests;

/// <summary>
/// L3 display-model regression tests (os/03): <see cref="SurfaceItem"/> wraps a folded
/// <see cref="SessionFold.Row"/> into what the Surface view binds — a role label and a Raw
/// JSON summary for the per-item detail panel. These pin the model so the rich view's data
/// contract is stable. Pure .NET (no WPF) so it runs headless.
/// </summary>
public class SurfaceItemTests
{
    private static SessionFold Row(params string[] recordJsons)
    {
        var fold = new SessionFold();
        fold.Reset();
        foreach (var line in recordJsons)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var ev = JsonDocument.Parse(line).RootElement;
            foreach (var e in ChunkRowExpander.Decode(ev)) fold.Fold(e);
        }
        fold.MaterializePendingRow();
        return fold;
    }

    [Theory]
    [InlineData("user", "用户")]
    [InlineData("assistant", "Assistant")]
    [InlineData("tool", "工具")]
    [InlineData("error", "错误")]
    [InlineData("turn", "轮次")]
    [InlineData("context", "context")]
    public void RoleLabel_maps_known_and_unknown_roles(string role, string expected)
    {
        var item = new SurfaceItem(new SessionFold.Row(role, "x"), 0);
        Assert.Equal(expected, item.RoleLabel);
    }

    /// <summary>
    /// Regression guard for the v0.2.2-alpha CI failure: role labels resolve through the resource
    /// set, so the host's ambient UI culture changes the answer, and the zh-CN fallback in
    /// <see cref="Dsh.App.Strings.Get"/> never kicks in for English because Strings.en.resx is
    /// complete. A windows-latest runner therefore produced "Tool" where the assertions above pin
    /// "工具". Both branches are selected explicitly here, so this test is culture-independent and
    /// documents the trap rather than depending on <see cref="TestCultureDefaults"/>.
    /// </summary>
    [Fact]
    public void RoleLabel_follows_the_explicit_language_not_the_host_culture()
    {
        var row = new SessionFold.Row("tool", "x");
        try
        {
            Localization.SetLanguage("en");
            Assert.Equal("Tool", new SurfaceItem(row, 0).RoleLabel);

            Localization.SetLanguage("zh-CN");
            Assert.Equal("工具", new SurfaceItem(row, 0).RoleLabel);
        }
        finally
        {
            Localization.SetLanguage("zh-CN");
        }
    }

    [Fact]
    public void RawJson_includes_role_text_and_index()
    {
        var item = new SurfaceItem(new SessionFold.Row("user", "hello world") { MessageId = "m1" }, 7);
        using var doc = JsonDocument.Parse(item.RawJson);
        var root = doc.RootElement;
        Assert.Equal(7, root.GetProperty("index").GetInt32());
        Assert.Equal("user", root.GetProperty("role").GetString());
        Assert.Equal("hello world", root.GetProperty("text").GetString());
        Assert.Equal("m1", root.GetProperty("messageId").GetString());
    }

    [Fact]
    public void RawJson_includes_tool_fields_for_tool_row()
    {
        var fold = Row(
            """{"type":"user/message","seq":1,"time":1,"data":{"content":"go","role":"user","id":"u"}}""",
            """{"type":"tool/call","seq":2,"time":2,"data":{"callId":"c1","name":"bash","arguments":"{\"cmd\":\"ls\"}"}}""",
            """{"type":"tool/result","seq":3,"time":3,"data":{"message":{"content":[{"type":"tool-result","toolCallId":"c1","content":[{"type":"text","text":"file.txt"}],"isError":false}]}}}""");
        var toolRow = fold.Rows.Single(r => r.Role == "tool");
        var item = new SurfaceItem(toolRow, 0);

        using var doc = JsonDocument.Parse(item.RawJson);
        var tool = doc.RootElement.GetProperty("tool");
        Assert.Equal("bash", tool.GetProperty("name").GetString());
        Assert.Equal("c1", tool.GetProperty("callId").GetString());
        Assert.Equal("done", tool.GetProperty("status").GetString());
    }

    /// <summary>
    /// Headless smoke test against a REAL on-disk session (if present under ~/.dsh). Exercises the
    /// full L1 decode + fold + SurfaceItem projection against genuine zstd/plain data — the exact
    /// path the rich Surface view consumes. Skipped when no session exists (CI-safe).
    /// </summary>
    [Fact]
    public void Surface_projection_handles_a_real_session_when_present()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "sessions");
        if (!Directory.Exists(root))
        {
            return; // no local sessions → nothing to smoke (not a failure)
        }
        var files = Directory.EnumerateFiles(root, "session.jsonl*", SearchOption.AllDirectories).Take(1).ToList();
        if (files.Count == 0) return;

        var fold = new SessionFold();
        fold.Reset();
        string path = files[0];
        bool isZstd = path.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase);
        int decodedLines = 0;
        foreach (var line in ZstdReader.ReadLines(path, isZstd))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonElement value;
            try { value = JsonDocument.Parse(line).RootElement; }
            catch { continue; }
            foreach (var e in ChunkRowExpander.Decode(value)) fold.Fold(e);
            decodedLines++;
        }
        fold.MaterializePendingRow();

        // Every folded row must project cleanly into a SurfaceItem (raw JSON must parse).
        int i = 0;
        foreach (var row in fold.Rows)
        {
            var item = new SurfaceItem(row, i++);
            using var _ = JsonDocument.Parse(item.RawJson);
        }
        Assert.True(decodedLines > 0, "expected at least one decoded line from a real session");
    }
}
