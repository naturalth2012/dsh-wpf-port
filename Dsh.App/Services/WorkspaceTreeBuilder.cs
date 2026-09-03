using Dsh.Contract.Methods;

namespace Dsh.App.Services;

/// <summary>
/// Pure, allocation-free grouping of <see cref="SessionSummary"/> under their owning
/// <see cref="WorkspaceView"/>. Centralized so the wire-shape rule (assign sessions via
/// <c>WorkspaceView.SessionIds</c>) lives in one testable place.
///
/// <para>The host's <c>session.list</c> currently ignores its <c>workspaceId</c> argument,
/// so a naive per-workspace <c>session.list</c> call yields the same global session set
/// every time. This helper relies on the authoritative per-workspace membership returned by
/// <c>workspace.list</c> instead, sidestepping that host-side gap without changing the wire.</para>
/// </summary>
public static class WorkspaceTreeBuilder
{
    /// <summary>A workspace group with the sessions that belong to it.</summary>
    /// <param name="WorkspaceId">Stable workspace id from <c>workspace.list</c>.</param>
    /// <param name="Title">Display title; falls back to <paramref name="Path"/> if empty.</param>
    /// <param name="Path">Absolute workspace path from <c>workspace.list</c>.</param>
    /// <param name="SessionIds">Sessions belonging to this workspace, in <c>workspace.list</c> order.</param>
    public sealed record Group(string WorkspaceId, string Title, string Path, string[] SessionIds);

    /// <summary>
    /// Group every session under the workspace whose <c>SessionIds</c> contains its id, or,
    /// when <c>SessionIds</c> is stale (host may not have updated the registry yet after a
    /// brand-new <c>session.create</c>), fall back to the workspace whose <c>path</c> is a
    /// prefix of the session's <c>cwd</c>. Sessions still not claimed after both passes are
    /// exposed via <see cref="Orphans"/> so the caller can decide how to surface them.
    /// </summary>
    public static IReadOnlyList<(Group Workspace, SessionSummary[] Sessions)> Assign(
        IReadOnlyList<WorkspaceView> workspaces,
        IReadOnlyList<SessionSummary> allSessions)
    {
        if (workspaces is null) throw new ArgumentNullException(nameof(workspaces));
        if (allSessions is null) throw new ArgumentNullException(nameof(allSessions));

        var byId = new Dictionary<string, SessionSummary>(allSessions.Count, StringComparer.Ordinal);
        foreach (var s in allSessions) byId[s.SessionId] = s;

        var result = new List<(Group, SessionSummary[])>(workspaces.Count);
        var claimed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var ws in workspaces)
        {
            var members = new List<SessionSummary>(ws.SessionIds.Length);
            foreach (var sid in ws.SessionIds)
            {
                if (byId.TryGetValue(sid, out var summary))
                {
                    members.Add(summary);
                    claimed.Add(sid);
                }
            }
            // Stable order matches WorkspaceView.SessionIds order; title falls back to path.
            string title = string.IsNullOrEmpty(ws.Title) ? ws.Path : ws.Title;
            result.Add((new Group(ws.WorkspaceId, title, ws.Path, ws.SessionIds), members.ToArray()));
        }

        // Pass 2: cwd-prefix matching for sessions not claimed by SessionIds (e.g. just after
        // session.create when host hasn't re-emitted workspace.list yet). Strict: cwd must be
        // a true descendant of the workspace path (cwd == wsPath is treated as a match; cwd like
        // `D:\dsh-test-old` does NOT match `D:\dsh-test`), to avoid mis-claiming sessions that
        // happen to share a path prefix with another workspace.
        var unclaimed = allSessions.Where(s => !claimed.Contains(s.SessionId)).ToArray();
        for (int i = 0; i < result.Count; i++)
        {
            var (group, members) = result[i];
            string wsPath = NormalizePrefix(group.Path);
            if (wsPath.Length == 0) continue;

            var extras = new List<SessionSummary>();
            foreach (var s in unclaimed)
            {
                string sCwd = NormalizePrefix(s.Cwd);
                if (sCwd.Length == 0) continue;
                bool match = sCwd.Equals(wsPath, StringComparison.OrdinalIgnoreCase)
                    || sCwd.StartsWith(wsPath + "\\", StringComparison.OrdinalIgnoreCase);
                if (match)
                {
                    extras.Add(s);
                    claimed.Add(s.SessionId);
                }
            }
            if (extras.Count > 0)
            {
                result[i] = (group, members.Concat(extras).ToArray());
            }
        }

        Orphans = unclaimed.Where(s => !claimed.Contains(s.SessionId)).ToArray();
        return result;
    }

    /// <summary>Sessions not claimed by any workspace (SessionIds or cwd-prefix fallback).</summary>
    public static SessionSummary[] Orphans { get; private set; } = Array.Empty<SessionSummary>();

    private static string NormalizePrefix(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return path.Replace('/', '\\').TrimEnd('\\');
    }

    /// <summary>Sessions present in <paramref name="allSessions"/> but not claimed by any workspace.</summary>
    public static SessionSummary[] FindOrphans(
        IReadOnlyList<WorkspaceView> workspaces,
        IReadOnlyList<SessionSummary> allSessions)
    {
        if (workspaces is null) throw new ArgumentNullException(nameof(workspaces));
        if (allSessions is null) throw new ArgumentNullException(nameof(allSessions));

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ws in workspaces)
        {
            foreach (var sid in ws.SessionIds) claimed.Add(sid);
        }
        return allSessions.Where(s => !claimed.Contains(s.SessionId)).ToArray();
    }
}