using System.Text.Json;
using Dsh.App.Services;
using Dsh.Contract.Methods;
using Xunit;

namespace Dsh.App.Tests;

/// <summary>
/// Tests for the workspace→session grouping rule. The host's <c>session.list</c> ignores
/// its workspaceId argument, so the only authoritative per-workspace membership is
/// <c>WorkspaceView.SessionIds</c> from <c>workspace.list</c>. This test pins that rule
/// against the real wire shapes.
/// </summary>
public class WorkspaceTreeBuilderTests
{
    /// <summary>Mirrors the production <c>JsonEnvelopeCodec</c> settings (camelCase).</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Fixture: the wire response captured against a real host on 2026-08-16. Four
    /// workspaces; one of them is the (harness-test) workspace with one session; the
    /// other three contain overlapping title collisons that must NOT cross over.
    /// </summary>
    private const string WorkspaceListWire = """
{
  "items": [
    { "workspaceId": "w-dshtest", "path": "C:\\\\fixtures\\\\dsh-test", "title": "dsh-test",
      "sessionIds": ["s-dshtest-1", "s-dshtest-2"],
      "createdAt": "2026-08-16T06:54:08.997Z", "updatedAt": "2026-08-16T12:02:40.487Z" },
    { "workspaceId": "w-harness", "path": "C:\\\\fixtures\\\\deepseek-harness", "title": "deepseek-harness",
      "sessionIds": ["s-harness-1"],
      "createdAt": "2026-08-16T04:33:18.158Z", "updatedAt": "2026-08-16T04:33:18.219Z" },
    { "workspaceId": "w-os-a", "path": "C:\\\\fixtures\\\\os-a", "title": "00-LocalOS",
      "sessionIds": ["s-os-a-1"],
      "createdAt": "2026-08-14T15:55:56.263Z", "updatedAt": "2026-08-15T07:19:49.579Z" },
    { "workspaceId": "w-os-b", "path": "C:\\\\fixtures\\\\os-b", "title": "00-LocalOS",
      "sessionIds": ["s-os-b-1", "s-os-b-2", "s-os-b-3"],
      "createdAt": "2026-08-14T13:36:50.383Z", "updatedAt": "2026-08-16T04:33:02.075Z" }
  ],
  "archivedSessionIds": []
}
""";

    /// <summary>Wire shape of session.list (unfiltered — host ignores workspaceId).</summary>
    private static string SessionListWire(IEnumerable<(string id, string? workspaceId)> sessions)
    {
        var items = string.Join(",", sessions.Select(s =>
            $@"{{ ""sessionId"": ""{s.id}"", ""updatedAt"": 1, ""running"": false, ""blank"": false }}"));
        return $@"{{ ""items"": [ {items} ] }}";
    }

    [Fact]
    public void Assign_sessions_only_to_their_declared_workspace()
    {
        var workspaces = JsonSerializer.Deserialize<WorkspaceListResponse>(WorkspaceListWire, Options)!.Items;
        var allWire = SessionListWire(new (string, string?)[]
        {
            ("s-dshtest-1", ""), ("s-dshtest-2", ""),
            ("s-harness-1", ""),
            ("s-os-a-1", ""),
            ("s-os-b-1", ""), ("s-os-b-2", ""), ("s-os-b-3", ""),
        });
        var allSessions = JsonSerializer.Deserialize<SessionListResponse>(allWire, Options)!.Items;

        var groups = WorkspaceTreeBuilder.Assign(workspaces, allSessions);

        Assert.Equal(4, groups.Count);

        // dsh-test owns exactly its two declared sessions — not the other workspaces' ones.
        Assert.Equal(new[] { "s-dshtest-1", "s-dshtest-2" },
            groups[0].Sessions.Select(s => s.SessionId));
        // harness owns exactly its one session.
        Assert.Equal(new[] { "s-harness-1" },
            groups[1].Sessions.Select(s => s.SessionId));
        // The two same-titled workspaces keep their respective memberships separate.
        Assert.Equal(new[] { "s-os-a-1" },
            groups[2].Sessions.Select(s => s.SessionId));
        Assert.Equal(new[] { "s-os-b-1", "s-os-b-2", "s-os-b-3" },
            groups[3].Sessions.Select(s => s.SessionId));
    }

    [Fact]
    public void Assign_returns_workspace_title_not_path()
    {
        var workspaces = JsonSerializer.Deserialize<WorkspaceListResponse>(WorkspaceListWire, Options)!.Items;
        var allWire = SessionListWire(new (string, string?)[] { ("s-dshtest-1", "") });
        var allSessions = JsonSerializer.Deserialize<SessionListResponse>(allWire, Options)!.Items;

        var groups = WorkspaceTreeBuilder.Assign(workspaces, allSessions);

        Assert.Equal("dsh-test", groups[0].Workspace.Title);
        Assert.Equal("deepseek-harness", groups[1].Workspace.Title);
        Assert.Equal("00-LocalOS", groups[2].Workspace.Title);
        Assert.Equal("00-LocalOS", groups[3].Workspace.Title);
    }

    [Fact]
    public void Assign_skips_session_ids_that_are_missing_from_session_list()
    {
        var workspaces = JsonSerializer.Deserialize<WorkspaceListResponse>(WorkspaceListWire, Options)!.Items;
        // session.list omits s-os-b-2 (e.g. host dropped it after archive).
        var allWire = SessionListWire(new (string, string?)[]
        {
            ("s-dshtest-1", ""), ("s-dshtest-2", ""),
            ("s-harness-1", ""),
            ("s-os-a-1", ""),
            ("s-os-b-1", ""), ("s-os-b-3", ""),
        });
        var allSessions = JsonSerializer.Deserialize<SessionListResponse>(allWire, Options)!.Items;

        var groups = WorkspaceTreeBuilder.Assign(workspaces, allSessions);

        Assert.Equal(new[] { "s-os-b-1", "s-os-b-3" },
            groups[3].Sessions.Select(s => s.SessionId));
    }

    [Fact]
    public void FindOrphans_returns_sessions_not_claimed_by_any_workspace()
    {
        var workspaces = JsonSerializer.Deserialize<WorkspaceListResponse>(WorkspaceListWire, Options)!.Items;
        var allWire = SessionListWire(new (string, string?)[]
        {
            ("s-dshtest-1", ""), ("s-dshtest-2", ""),
            ("s-harness-1", ""),
            ("s-os-a-1", ""),
            ("s-os-b-1", ""), ("s-os-b-2", ""), ("s-os-b-3", ""),
            ("s-orphan", null!), // not in any workspace's SessionIds
        });
        var allSessions = JsonSerializer.Deserialize<SessionListResponse>(allWire, Options)!.Items;

        var orphans = WorkspaceTreeBuilder.FindOrphans(workspaces, allSessions);

        Assert.Equal(new[] { "s-orphan" }, orphans.Select(s => s.SessionId));
    }

    [Fact]
    public void Assign_throws_on_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() =>
            WorkspaceTreeBuilder.Assign(null!, Array.Empty<SessionSummary>()));
        Assert.Throws<ArgumentNullException>(() =>
            WorkspaceTreeBuilder.Assign(Array.Empty<WorkspaceView>(), null!));
        Assert.Throws<ArgumentNullException>(() =>
            WorkspaceTreeBuilder.FindOrphans(null!, Array.Empty<SessionSummary>()));
        Assert.Throws<ArgumentNullException>(() =>
            WorkspaceTreeBuilder.FindOrphans(Array.Empty<WorkspaceView>(), null!));
    }

    [Fact]
    public void Assign_falls_back_to_cwd_prefix_when_SessionIds_missing()
    {
        // Right after session.create the host may not have re-emitted workspace.list with the
        // new id in SessionIds. The builder must still place the session under the workspace
        // whose path is a prefix of the session's cwd.
        var workspaces = new[]
        {
            MakeWorkspace("ws-1", "dsh-test", @"D:\dsh-test"),
        };
        var allSessions = new[]
        {
            MakeSummary("s-new", cwd: @"D:\dsh-test\new-subdir"),
        };

        var groups = WorkspaceTreeBuilder.Assign(workspaces, allSessions);

        Assert.Single(groups);
        Assert.Single(groups[0].Sessions);
        Assert.Equal("s-new", groups[0].Sessions[0].SessionId);
        Assert.Empty(WorkspaceTreeBuilder.Orphans);
    }

    [Fact]
    public void Assign_reports_sessions_as_orphans_when_no_match()
    {
        // Cwd is outside any registered workspace path → reported as orphan.
        var workspaces = new[]
        {
            MakeWorkspace("ws-1", "dsh-test", @"D:\dsh-test"),
        };
        var allSessions = new[]
        {
            MakeSummary("s-loose", cwd: @"E:\elsewhere"),
        };

        var groups = WorkspaceTreeBuilder.Assign(workspaces, allSessions);

        Assert.Single(groups);
        Assert.Empty(groups[0].Sessions);
        Assert.Single(WorkspaceTreeBuilder.Orphans);
        Assert.Equal("s-loose", WorkspaceTreeBuilder.Orphans[0].SessionId);
    }

    [Fact]
    public void Assign_treats_trailing_separators_and_forward_slashes_equivalently()
    {
        // Windows path comparisons should ignore trailing slash and `/` vs `\`.
        var workspaces = new[]
        {
            MakeWorkspace("ws-1", "dsh-test", @"D:/dsh-test/"),
        };
        var allSessions = new[]
        {
            MakeSummary("s-new", cwd: @"D:\dsh-test"),
        };

        var groups = WorkspaceTreeBuilder.Assign(workspaces, allSessions);

        Assert.Single(groups[0].Sessions);
        Assert.Empty(WorkspaceTreeBuilder.Orphans);
    }

    [Fact]
    public void Assign_does_not_misclaim_sibling_workspaces_with_shared_prefix()
    {
        // Sibling paths like `D:\dsh-test` vs `D:\dsh-test-old` must NOT be mis-attributed
        // to each other — cwd-prefix fallback must require a true descendant.
        var workspaces = new[]
        {
            MakeWorkspace("ws-1", "dsh-test", @"D:\dsh-test"),
            MakeWorkspace("ws-2", "dsh-test-old", @"D:\dsh-test-old"),
        };
        var allSessions = new[]
        {
            MakeSummary("s-orphan", cwd: @"D:\dsh-test-old\sub"),
        };

        var groups = WorkspaceTreeBuilder.Assign(workspaces, allSessions);

        Assert.Empty(groups[0].Sessions); // dsh-test should NOT claim s-orphan
        Assert.Single(groups[1].Sessions); // dsh-test-old should claim it
        Assert.Equal("s-orphan", groups[1].Sessions[0].SessionId);
        Assert.Empty(WorkspaceTreeBuilder.Orphans);
    }

    [Fact]
    public void Assign_blank_session_uses_placeholder_default_title()
    {
        // The placeholder default-title ("新会话" / "(无标题)") lives in MainViewModel
        // (SummaryTitle), so this test only verifies that blank sessions still pass
        // through Assign — the rendering concern is covered in MainViewModel.
        var workspaces = new[]
        {
            MakeWorkspace("ws-1", "dsh-test", @"D:\dsh-test"),
        };
        var summary = MakeSummary("s-new", cwd: @"D:\dsh-test");
        var withBlank = summary with { Blank = true };

        var groups = WorkspaceTreeBuilder.Assign(workspaces, new[] { withBlank });

        Assert.Single(groups[0].Sessions);
        Assert.True(groups[0].Sessions[0].Blank);
    }

    private static WorkspaceView MakeWorkspace(string id, string title, string path) => new()
    {
        WorkspaceId = id,
        Title = title,
        Path = path,
        SessionIds = Array.Empty<string>(),
        CreatedAt = "2026-01-01T00:00:00Z",
        UpdatedAt = "2026-01-01T00:00:00Z",
    };

    private static SessionSummary MakeSummary(string id, string? cwd) => new()
    {
        SessionId = id,
        UpdatedAt = 0L,
        Running = false,
        Blank = true,
        Cwd = cwd,
    };
}