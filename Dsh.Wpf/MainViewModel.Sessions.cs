using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.App;
using Dsh.App.Services;
using Loc = Dsh.App.Services.Localization;
using Dsh.Client;
using Dsh.Contract.Frames;
using Dsh.Contract.Methods;
using Dsh.Contract.Projections;
using Dsh.Wpf.Controls;
using Dsh.Contract.Rpc;
using Microsoft.Extensions.Logging;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace Dsh.Wpf;

/// <summary>
/// Main-window view model: drives the MVP loop (connect → list sessions → prompt → stream
/// assistant text). Owns the client, the projection store, and the interaction coordinator,
/// and marshals downstream WebSocket frames onto the UI thread.
/// </summary>
public partial class MainViewModel : ObservableObject
{

    /// <summary>Insert a chosen command name into the composer (keeping any trailing args).</summary>
    [RelayCommand]
    private void ApplyCommand(CommandEntry? entry)
    {
        if (entry is null) return;
        string t = InputText ?? "";
        // P2-3: "@" references insert with "@", "/" commands with "/".
        bool atRef = t.StartsWith('@');
        string frag = t.Length > 1 ? t[1..].TrimStart() : "";
        string trailing = frag.Length > 0 && !entry.Name.StartsWith(frag, StringComparison.OrdinalIgnoreCase)
            ? ""
            : (frag.Length > entry.Name.Length ? frag[entry.Name.Length..].TrimStart() : "");
        InputText = (atRef ? "@" : "/") + entry.Name + (trailing.Length > 0 ? " " + trailing : "");
        CommandMatches.Clear();
        SelectedCommandIndex = -1;
        ComposerFocusRequested?.Invoke(this, EventArgs.Empty); // P1-7: return focus to the composer.
    }

    /// <summary>Move the <c>/</c> candidate selection up/down (P1-7). Delta = ±1.</summary>
    [RelayCommand]
    private void MoveCommandSelection(int delta)
    {
        if (CommandMatches.Count == 0) return;
        int next = Math.Clamp(SelectedCommandIndex + delta, 0, CommandMatches.Count - 1);
        SelectedCommandIndex = next;
    }

    /// <summary>Apply the currently selected <c>/</c> candidate (P1-7, Enter).</summary>
    [RelayCommand]
    private void ApplySelectedCommand()
    {
        if (SelectedCommandIndex >= 0 && SelectedCommandIndex < CommandMatches.Count)
        {
            ApplyCommand(CommandMatches[SelectedCommandIndex]);
        }
    }

    /// <summary>Dismiss the <c>/</c> candidate list (P1-7, Esc).</summary>
    [RelayCommand]
    private void ClearCommandMenu()
    {
        CommandMatches.Clear();
        SelectedCommandIndex = -1;
    }

    /// <summary>Set positive feedback on a message (H5, P2-4: messageFeedback.put).</summary>
    [RelayCommand]
    private Task SetPositiveFeedbackAsync(ChatEntry? entry)
        => SetMessageFeedbackAsync(entry, Dsh.Contract.Methods.MessageFeedbackRating.Positive);

    /// <summary>Set negative feedback on a message (H5, P2-4: messageFeedback.put).</summary>
    [RelayCommand]
    private Task SetNegativeFeedbackAsync(ChatEntry? entry)
        => SetMessageFeedbackAsync(entry, Dsh.Contract.Methods.MessageFeedbackRating.Negative);

    private async Task SetMessageFeedbackAsync(ChatEntry? entry, Dsh.Contract.Methods.MessageFeedbackRating rating)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.MessageId) || string.IsNullOrWhiteSpace(SelectedSessionId)) return;
        try
        {
            var item = await _sessions.PutMessageFeedback(SelectedSessionId, entry.MessageId, rating);
            ConnectionStatus = rating == Dsh.Contract.Methods.MessageFeedbackRating.Positive
                ? Loc.Get("Sess.MarkedHelpful")
                : Loc.Get("Sess.MarkedNeedsWork");
        }
        catch (Exception ex)
        {
            NotifyErrorKey("Notify.FeedbackFailed", ex.Message);
        }
    }

    /// <summary>Copy a message's text to the clipboard (C18, P2: message actions).</summary>
    [RelayCommand]
    private void CopyMessageText(ChatEntry? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.Text)) return;
        try
        {
            System.Windows.Clipboard.SetText(entry.Text);
            ConnectionStatus = Loc.Get("Conn.CopyMessage");
        }
        catch (Exception ex)
        {
            NotifyErrorKey("Notify.CopyFailed", ex.Message);
        }
    }

    /// <summary>Copy a rendered code block's source text to the clipboard (M4 code bar).</summary>
    [RelayCommand]
    private void CopyCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        try
        {
            System.Windows.Clipboard.SetText(code);
            ConnectionStatus = Loc.Get("Conn.CopyMessage");
        }
        catch (Exception ex)
        {
            NotifyErrorKey("Notify.CopyFailed", ex.Message);
        }
    }

    /// <summary>
    /// Open the original web client in the system browser (H1). The web UI is served at the
    /// same origin as the host, so we open <see cref="HostUrl"/> (falling back to the default
    /// loopback address when it is empty).
    /// </summary>
    [RelayCommand]
    private void OpenWeb()
    {
        try
        {
            var url = string.IsNullOrWhiteSpace(HostUrl) ? "http://127.0.0.1:3080" : HostUrl;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
            ConnectionStatus = Loc.Format("Sess.OpenedInBrowser", url);
        }
        catch (Exception ex)
        {
            NotifyErrorKey("Notify.OpenBrowserFailed", ex.Message);
        }
    }

    /// <summary>Open a file path (H4 inline file mention / H3 produced file) with the OS default app.</summary>
    [RelayCommand]
    private async Task OpenFileAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        // The AI often returns relative paths (e.g. "today_date.txt"); resolve against the
        // session cwd or harness root so the host's PowerShell call sees a real path.
        var sessionCwd = Sessions.FirstOrDefault(s => s.Id == SelectedSessionId)?.Cwd;
        path = FilePathResolver.Resolve(path, sessionCwd, HarnessDirectory);
        try
        {
            var result = await _sessions.OpenPath(path);
            if (result.Opened)
            {
                ConnectionStatus = Loc.Format("Sess.OpenedPath", path);
            }
        }
        catch (Exception ex)
        {
            NotifyErrorKey("Notify.OpenFailed", ex.Message);
        }
    }

    /// <summary>Open a produced file's path with the OS default app (H3, P2-5).</summary>
    [RelayCommand]
    private async Task OpenDeliverableAsync(SessionFold.DeliverableItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path)) return;
        // Same relative-path resolution as OpenFileAsync.
        var sessionCwd = Sessions.FirstOrDefault(s => s.Id == SelectedSessionId)?.Cwd;
        var path = FilePathResolver.Resolve(item.Path, sessionCwd, HarnessDirectory);
        try
        {
            var result = await _sessions.OpenPath(path);
            if (result.Opened)
            {
                ConnectionStatus = Loc.Format("Sess.OpenedPath", path);
            }
        }
        catch (Exception ex)
        {
            NotifyErrorKey("Notify.OpenDeliverableFailed", ex.Message);
        }
    }

    /// <summary>
    /// Open a session's working directory with the OS default app (C9, host.openPath).
    /// Requires a cwd; the host only honors it on a loopback connection.
    /// </summary>
    [RelayCommand]
    private async Task OpenFolderAsync(SessionItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Cwd)) return;
        try
        {
            var result = await _sessions.OpenPath(item.Cwd);
            if (result.Opened)
            {
                ConnectionStatus = Loc.Format("Sess.OpenedPath", item.Cwd);
            }
        }
        catch (Exception ex)
        {
            ConnectionStatus = Loc.Format("Sess.OpenFolderFailed", ex.Message);
            NotifyErrorKey("Notify.OpenFolderFailed", ex.Message);
        }
    }

    /// <summary>
    /// Export the client diagnostic log (P2-17): copies dsh-client.log to a timestamped
    /// file alongside a small environment summary, then opens the folder in Explorer.
    /// Never throws — failures surface as a status message.
    /// </summary>
    [RelayCommand]
    private void ExportDiagnostics()
    {
        try
        {
            var source = Path.Combine(AppContext.BaseDirectory, "dsh-client.log");
            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
            var destName = $"dsh-client-diagnostics-{stamp}.log";
            var dest = Path.Combine(AppContext.BaseDirectory, destName);

            var summary = BuildDiagnosticsSummary();
            File.WriteAllText(dest, summary);

            if (File.Exists(source))
            {
                File.Copy(source, dest, overwrite: true);
                var summaryPath = Path.Combine(AppContext.BaseDirectory, $"dsh-client-summary-{stamp}.txt");
                File.WriteAllText(summaryPath, summary);
                NotifySuccessKey("Notify.DiagExportDone", destName);
            }
            else
            {
                NotifySuccessKey("Notify.DiagExportSummaryOnly", destName);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{dest}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            NotifyErrorKey("Notify.DiagExportFailed", ex.Message);
        }
    }

    /// <summary>Build a plain-text snapshot of client environment state for the diagnostics export.</summary>
    private string BuildDiagnosticsSummary()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Dsh WPF Client — Diagnostics Summary");
        sb.AppendLine($"Exported : {DateTimeOffset.Now:O}");
        sb.AppendLine($"BaseDir  : {AppContext.BaseDirectory}");
        sb.AppendLine($"HostUrl  : {HostUrl}");
        sb.AppendLine($"Connected: {IsConnected}");
        sb.AppendLine($"Model    : {SelectedModelId}");
        sb.AppendLine($"Launcher : {HarnessDirectory}");
        sb.AppendLine($"Sessions : {Sessions.Count}");
        sb.AppendLine();
        sb.AppendLine("--- recent client log (if present) follows in the .log file ---");
        return sb.ToString();
    }

    /// <summary>Copy a session's working-directory path to the clipboard (P2-2).</summary>
    [RelayCommand]
    private void CopySessionPath(SessionItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Cwd)) return;
        Clipboard.SetText(item.Cwd);
        NotifySuccess(Loc.Get("Conn.CopySessionPath"));
    }

    /// <summary>Copy a session's title to the clipboard (P2-2).</summary>
    [RelayCommand]
    private void CopySessionTitle(SessionItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Title)) return;
        Clipboard.SetText(item.Title);
        NotifySuccess(Loc.Get("Conn.CopySessionTitle"));
    }

    /// <summary>Copy a workspace's path to the clipboard (P2-2).</summary>
    [RelayCommand]
    private void CopyWorkspacePath(WorkspaceNode? node)
    {
        if (node is null || string.IsNullOrWhiteSpace(node.Path)) return;
        Clipboard.SetText(node.Path);
        NotifySuccess(Loc.Get("Conn.CopyWorkspacePath"));
    }

    /// <summary>
    /// Archive a session (B6, workspace.archiveSession). The session leaves the grouping
    /// surfaces but keeps its log; a future unarchive restores its position.
    /// </summary>
    [RelayCommand]
    private async Task ArchiveSessionAsync(SessionItem? item)
    {
        if (item is null) return;
        try
        {
            var result = await _sessions.ArchiveSession(item.Id);
            ConnectionStatus = Loc.Format("Sess.Archived", item.Id, result.ArchivedSessionIds.Length);
            await RefreshSessionsAsync();
        }
        catch (Exception ex)
        {
            ConnectionStatus = Loc.Format("Sess.ArchiveFailed", ex.Message);
            NotifyErrorKey("Notify.ArchiveFailed", ex.Message);
        }
    }

    /// <summary>
    /// Fork a session (B7, session.fork) at the latest completed-turn boundary, then open the
    /// new child session. Mirrors the Web client's right-click → fork → open flow.
    /// </summary>
    [RelayCommand]
    private async Task ForkSessionAsync(SessionItem? item)
    {
        if (item is null) return;
        try
        {
            ConnectionStatus = Loc.Format("Sess.Forking", item.Id);
            var result = await _sessions.Fork(item.Id);
            ConnectionStatus = Loc.Format("Sess.Forked", result.SessionId);
            await RefreshWorkspacesAsync();
            await RefreshSessionsAsync();
            SelectedSessionId = result.SessionId;
            await LoadHistoryAsync(result.SessionId);
            await LoadModelsAsync();
            await LoadSubagentsAsync();
        }
        catch (Exception ex)
        {
            ConnectionStatus = Loc.Format("Sess.ForkFailed", ex.Message);
            NotifyErrorKey("Notify.ForkFailed", ex.Message);
        }
    }

    /// <summary>
    /// Add a workspace by adopting an existing directory (B10). Uses host.pickDirectory
    /// (loopback Windows IFileOpenDialog) to choose, then workspace.create to register it.
    /// </summary>
    [RelayCommand]
    private async Task AddWorkspaceAsync()
    {
        try
        {
            ConnectionStatus = Loc.Get("Conn.PickWorkspaceDir");
            var picked = await _sessions.PickDirectory();
            if (string.IsNullOrWhiteSpace(picked.Path))
            {
                ConnectionStatus = Loc.Get("Conn.PickCancelled");
                return;
            }
            var created = await _sessions.CreateWorkspace(picked.Path);
            // Target the next New Session at this fresh workspace even before its tree node is
            // visible (the host may not have created its blank session yet).
            _lastAddedWorkspacePath = created.Workspace.Path;
            ConnectionStatus = created.Created
                ? Loc.Format("Sess.AddedWorkspace", created.Workspace.Title)
                : Loc.Format("Sess.WorkspaceExists", created.Workspace.Title);
            await RefreshWorkspacesAsync();
            await RefreshSessionsAsync();
        }
        catch (Exception ex)
        {
            ConnectionStatus = Loc.Format("Sess.AddWorkspaceFailed", ex.Message);
            NotifyErrorKey("Notify.AddWorkspaceFailed", ex.Message);
        }
    }

    /// <summary>
    /// Delete a workspace from the registry (B8, workspace.delete). The underlying directory
    /// and session logs are untouched — only the grouping surface is removed. Confirms first.
    /// </summary>
    [RelayCommand]
    private async Task DeleteWorkspaceAsync(WorkspaceNode? node)
    {
        if (node is null) return;
        var answer = MessageBox.Show(
            Loc.Format("Ws.DeleteConfirm", node.Title),
            Loc.Get("Ws.DeleteTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            var result = await _sessions.DeleteWorkspace(node.WorkspaceId);
            ConnectionStatus = result.Deleted ? Loc.Format("Sess.DeletedWorkspace", node.Title) : Loc.Format("Sess.DeleteWorkspaceFailed", node.Title);
            await RefreshWorkspacesAsync();
            await RefreshSessionsAsync();
        }
        catch (Exception ex)
        {
            ConnectionStatus = Loc.Format("Sess.DeleteWorkspaceError", ex.Message);
            NotifyErrorKey("Notify.DeleteWorkspaceFailed", ex.Message);
        }
    }

    /// <summary>
    /// Merge duplicate workspaces returned by host (same <c>workspaceId</c>): host's
    /// workspace.list sometimes emits the same id twice (registry staleness), which would
    /// otherwise produce two sidebar nodes for the same workspace. We keep one and union
    /// <c>SessionIds</c> so the merged view still contains every session id.
    /// </summary>
    private static IReadOnlyList<WorkspaceView> DedupeWorkspaces(IReadOnlyList<WorkspaceView> workspaces)
    {
        var byId = new Dictionary<string, WorkspaceView>(workspaces.Count, StringComparer.Ordinal);
        foreach (var w in workspaces)
        {
            if (byId.TryGetValue(w.WorkspaceId, out var existing))
            {
                var mergedIds = existing.SessionIds
                    .Concat(w.SessionIds)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                byId[w.WorkspaceId] = existing with { SessionIds = mergedIds };
            }
            else
            {
                byId[w.WorkspaceId] = w;
            }
        }
        return byId.Values.ToArray();
    }

    /// <summary>Build the workspace tree (workspace.list) with each workspace's sessions.</summary>
    private async Task RefreshWorkspacesAsync()
    {
        // NOTE: do NOT Clear() the Workspaces collection here. Two concurrent calls (e.g. user
        // connect + a reconnect-triggered refresh) would each Clear() then Add() their own result,
        // producing duplicate workspace nodes in the tree. Instead, build the new list off-thread
        // and atomically replace the collection in a single Dispatcher call.
        try
        {
            // Fetch workspace list and a single unfiltered session list in parallel off the UI
            // thread. The host's session.list currently ignores its workspaceId argument, so we
            // route assignments through WorkspaceView.SessionIds (the authoritative per-workspace
            // membership returned by workspace.list) via the testable WorkspaceTreeBuilder.
            var workspacesTask = _sessions.ListWorkspaces();
            var allSessionsTask = _sessions.List();
            await Task.WhenAll(workspacesTask, allSessionsTask).ConfigureAwait(false);

            // P2-11: persist the latest snapshot so the sidebar can be browsed offline.
            OfflineCache.Save(workspacesTask.Result.Items, allSessionsTask.Result.Items);

            var deduped = DedupeWorkspaces(workspacesTask.Result.Items);
            var groups = WorkspaceTreeBuilder.Assign(deduped, allSessionsTask.Result.Items);

            // Build the replacement node list off the UI thread, then atomically swap it in.
            var newNodes = BuildWorkspaceNodes(groups, WorkspaceTreeBuilder.Orphans);

            // ObservableCollection must be touched only on the UI thread — marshal back and
            // replace the whole collection atomically. Concurrent callers' swaps will overwrite
            // each other but never produce duplicates.
            await Application.Current.Dispatcher.InvokeAsync(() => SwapWorkspaceTree(newNodes));
        }
        catch (Exception ex)
        {
            // P2-11: offline fallback — if the live list fails, browse from the last snapshot
            // and don't surface an error (the user sees cached sessions). Only show the raw
            // failure when no cache exists.
            if (TryBuildFromCache()) return;

            // Marshaled to the UI thread (E4: InvokeAsync — nothing on this thread waits on the
            // result, so a blocking Invoke would just stall the worker unnecessarily).
            // after ConfigureAwait(false) this code runs on a thread-pool thread, and
            // Rows/Transcript are WPF-bound collections.
            _ = Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _fold.Rows.Add(new SessionFold.Row("error", Loc.Format("Sess.LoadWorkspaceFailed", ex.Message)));
                SyncFoldToUi();
            });
        }
    }

    /// <summary>
    /// Rebuild the workspace tree from the last offline snapshot (P2-11). Returns true when a
    /// cache was loaded and the tree swapped in; false when no cache exists (let the caller
    /// surface the original error).
    /// </summary>
    private bool TryBuildFromCache()
    {
        var snapshot = OfflineCache.TryLoad();
        if (snapshot is null) return false;

        var deduped = DedupeWorkspaces(snapshot.Workspaces);
        var groups = WorkspaceTreeBuilder.Assign(deduped, snapshot.Sessions);
        var newNodes = BuildWorkspaceNodes(groups, WorkspaceTreeBuilder.Orphans);

        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            SwapWorkspaceTree(newNodes);
        });
        return true;
    }

    /// <summary>
    /// Build the sidebar node list from assigned groups + orphan sessions (shared by the live
    /// refresh and the offline-cache path so the two cannot drift apart).
    /// </summary>
    private static List<WorkspaceNode> BuildWorkspaceNodes(
        IReadOnlyList<(WorkspaceTreeBuilder.Group Workspace, SessionSummary[] Sessions)> groups,
        IReadOnlyList<SessionSummary> orphans)
    {
        var newNodes = new List<WorkspaceNode>(groups.Count + 1);
        foreach (var (group, members) in groups)
        {
            var node = new WorkspaceNode(group.WorkspaceId, group.Title, group.Path);
            // Mirror the Web client: blank sessions are not rendered as sidebar rows at all.
            // The "New Session" affordance in the UI is the dedicated \uE8F1 button in the top
            // toolbar (MainWindow.xaml:431), not a session list entry. Previously this code
            // collapsed blanks by cwd to keep "at most one" per workspace, but the Web does not
            // show any blank row — so filtering them out entirely matches the Web exactly.
            foreach (var summary in members)
            {
                if (summary.Blank) continue;
                node.Sessions.Add(ToSessionItem(summary));
            }
            newNodes.Add(node);
        }
        var orphanNode = new WorkspaceNode(OrphanWorkspaceId, Loc.Get("Sess.Untitled"), "");
        foreach (var summary in orphans)
        {
            if (summary.Blank) continue;
            orphanNode.Sessions.Add(ToSessionItem(summary));
        }
        newNodes.Add(orphanNode);
        return newNodes;
    }

    /// <summary>
    /// Atomically replace the sidebar on the UI thread, preserving the current selection when
    /// the selected session survives, otherwise auto-focusing the first session.
    /// </summary>
    private void SwapWorkspaceTree(List<WorkspaceNode> newNodes)
    {
        var preserve = SelectedSessionId;
        Workspaces.Clear();
        foreach (var n in newNodes) Workspaces.Add(n);
        if (preserve is not null && Workspaces.SelectMany(w => w.Sessions).Any(s => s.Id == preserve))
        {
            (System.Windows.Application.Current.MainWindow as MainWindow)?.ReselectSession(preserve);
        }
        else if (SelectedSessionId is null)
        {
            var first = Workspaces.SelectMany(w => w.Sessions).FirstOrDefault();
            if (first is not null) SelectedSessionId = first.Id;
        }
    }

    /// <summary>Map a session summary to a sidebar row (shared by live refresh and offline cache).</summary>
    private static SessionItem ToSessionItem(SessionSummary summary) => new()
    {
        Id = summary.SessionId,
        Title = SummaryTitle(summary),
        Running = summary.Running,
        Cwd = summary.Cwd,
        Blank = summary.Blank,
        UpdatedAtText = FormatUpdatedAt(summary.UpdatedAt),
    };

    /// <summary>Extracts the session title from its projections block, or a placeholder.</summary>
    private static string SummaryTitle(SessionSummary summary)
    {
        if (summary.Projections is JsonElement proj &&
            proj.TryGetProperty("values", out var values) &&
            values.TryGetProperty("title", out var t) &&
            t.ValueKind == JsonValueKind.String)
        {
            return t.GetString() ?? DefaultTitle(summary);
        }
        return DefaultTitle(summary);
    }

    /// <summary>Default placeholder: blank sessions render as "新会话" (matches Web UI), otherwise "(无标题)".</summary>
    private static string DefaultTitle(SessionSummary summary) =>
        summary.Blank ? Loc.Get("Sess.NewSession") : Loc.Get("Sess.NoTitle");

    /// <summary>
    /// Read the session-stats / token-usage projections for the selected session (P2-7) and
    /// format a compact status line. Empty when the host has not pushed the projections (the
    /// current contract gap — 02 C17), so the status bar simply omits it.
    /// </summary>
    private void RefreshSessionStats()
    {
        if (SelectedSessionId is null)
        {
            SessionStatsText = "";
            RefreshPlanAndPermissions(null, null);
            return;
        }
        try
        {
            var stats = _projections.Get<SessionStatsProjection>(SelectedSessionId, ProjectionKeys.SessionStats);
            var tokens = _projections.Get<TokenUsageProjection>(SelectedSessionId, ProjectionKeys.TokenUsage);
            var pressure = _projections.Get<ContextPressureProjection>(SelectedSessionId, ProjectionKeys.ContextPressure);
            SessionStatsText = SessionStatsFormatter.Format(stats, tokens);
            // C17 rich line: fraction + detail from the same projections (pure, testable logic).
            var (fraction, detail) = SessionStatsFormatter.FormatRich(stats, tokens, pressure);
            HasRichStats = stats is not null || tokens is not null || pressure is not null;
            StatsRingFraction = fraction ?? 0;
            StatsRingText = fraction is > 0 ? $"{fraction.Value * 100.0:0}%" : "";
            StatsDetailText = detail;
            // Precise ring tooltip: exact used/window tokens (Web ContextMeter hover), or a
            // short hint when the window size is unknown.
            long? usedTokens = pressure?.ProjectedTokens ?? pressure?.PressureTokens;
            if (usedTokens is > 0)
            {
                string usedLabel = SessionStatsFormatter.FormatTokenCount(usedTokens.Value);
                string pctPrefix = fraction is > 0 ? $"{fraction.Value * 100.0:0}% · " : "";
                StatsRingTooltip = pressure?.ContextWindow is > 0
                    ? $"{pctPrefix}{usedLabel} / {SessionStatsFormatter.FormatTokenCount(pressure.ContextWindow.Value)} {Dsh.App.Services.Localization.Get("Stats.TokensUnit")}"
                    : $"{usedLabel} {Dsh.App.Services.Localization.Get("Stats.TokensUnit")}";
            }
            else
            {
                StatsRingTooltip = "";
            }
            // D3 plan / D5 permission chips share the same projection store.
            RefreshPlanAndPermissions(
                _projections.Get<PlanProjection>(SelectedSessionId, ProjectionKeys.Plan),
                _projections.Get<PermissionSelect>(SelectedSessionId, ProjectionKeys.Permissions));
        }
        catch (Exception ex)
        {
            // Never let a malformed/partial projection crash the UI-thread frame handler;
            // just omit the status line for this turn.
            Logging.Get<MainViewModel>().LogWarning(ex, "RefreshSessionStats failed");
            SessionStatsText = "";
        }
    }

    /// <summary>
    /// D3/D5: sync the plan chip and permission preset chips from the selected session's
    /// projections. Values may be null when the domain plugin is unmounted (host has not pushed
    /// that projection), in which case the corresponding UI is hidden.
    /// </summary>
    private void RefreshPlanAndPermissions(PlanProjection? plan, PermissionSelect? perm)
    {
        IsPlanMode = plan?.Active == true;
        if (perm is not null)
        {
            PermissionOptions.Clear();
            foreach (var o in perm.Options)
                PermissionOptions.Add(o);
            PermissionCurrent = perm.CurrentValue;
            HasPermissionSelect = true;
        }
        else
        {
            PermissionOptions.Clear();
            PermissionCurrent = "";
            HasPermissionSelect = false;
        }
    }

    /// <summary>
    /// Format a session's epoch-ms <c>UpdatedAt</c> into a short local time string for the
    /// hover card (P1-1). Returns empty when the value is out of a sane range (e.g. zero/absent).
    /// </summary>
    private static string FormatUpdatedAt(long epochMs)
    {
        if (epochMs <= 0) return "";
        try
        {
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).ToLocalTime();
            // K3: format follows the active UI culture — zh-CN → "2026-08-18 14:30",
            // en → culture-appropriate short date/time.
            var culture = Dsh.App.Services.Localization.Culture;
            var pattern = culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? "yyyy-MM-dd HH:mm"
                : "yyyy/M/d HH:mm";
            return dt.ToString(pattern, culture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "";
        }
    }

}
