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

    [RelayCommand]
    private async Task ApproveOnceAsync()
    {
        if (PendingApproval is null) return;
        var p = PendingApproval;
        try
        {
            await _interactions.RespondApproval(p.Frame.RpcId!, p.Frame.SessionId, p.Frame.ApprovalId, allowedOnce: true);
            PendingApproval = null;
            SetSessionWaiting(p.Frame.SessionId, false); // P1-2: amber dot clears on approval.
        }
        catch (Exception ex)
        {
            _fold.Rows.Add(new SessionFold.Row("error", $"审批失败：{ex.Message}"));
            NotifyErrorKey("Notify.ApproveFailed", ex.Message);
            SyncFoldToUi();
        }
    }

    [RelayCommand]
    private async Task RejectAsync()
    {
        if (PendingApproval is null) return;
        var p = PendingApproval;
        try
        {
            await _interactions.RespondApproval(p.Frame.RpcId!, p.Frame.SessionId, p.Frame.ApprovalId, allowedOnce: false);
            PendingApproval = null;
            SetSessionWaiting(p.Frame.SessionId, false); // P1-2: amber dot clears on reject.
        }
        catch (Exception ex)
        {
            _fold.Rows.Add(new SessionFold.Row("error", $"拒绝失败：{ex.Message}"));
            NotifyErrorKey("Notify.RejectFailed", ex.Message);
            SyncFoldToUi();
        }
    }

    /// <summary>
    /// Connection-state change from the shared scope (P1-14/P1-15). On "up" (reconnect) each
    /// window re-pulls its own tree/sessions; on "down" it clears the transient flags and shows
    /// the drop. The stream loop and handshake live in <see cref="ConnectionScope"/>.
    /// </summary>
    private void OnScopeConnectionStateChanged(bool up)
    {
        if (up)
        {
            ConnectionStatus = Loc.Get("Conn.Connected");
            IsConnected = true;
            NotifySuccess(Loc.Get("Conn.Reconnected"));
            _ = RefreshWorkspacesAsync();
            _ = RefreshSessionsAsync();
            if (SelectedSessionId is { } sid)
            {
                _ = LoadHistoryAsync(sid);
                _ = LoadModelsAsync();
                _ = LoadSubagentsAsync();
            }
        }
        else
        {
            ConnectionStatus = Loc.Get("Conn.Reconnecting");
            IsConnected = false;
            NotifyInfo(Loc.Get("Conn.Reconnecting"));
            // Reset transient per-window state for the new generation. NOTE (E2): we deliberately
            // keep _projections (the per-session projection store). Projections are host-pushed,
            // seq-tagged values (stats / plan / permission / credentials); dropping them on a
            // transient reconnect would blank the stats line, plan & permission chips and
            // credential dots until the next projection frame arrives. Reconnecting frames carry
            // fresh seq numbers, so higher-seq-wins keeps the store coherent. Only the ephemeral
            // interaction state (pending approvals/questions, subagent flags) is cleared.
            _interactions.Clear();
            Jobs.Clear();
            PendingApproval = null;
            PendingQuestion = null;
            PendingQuestions.Clear();
            foreach (var ws in Workspaces)
            {
                foreach (var s in ws.Sessions)
                {
                    s.Waiting = false;
                    s.JobRunning = false;
                }
            }
        }
    }

    private void OnScopeMuxFrame(MuxFrame frame)
    {
        // Defense-in-depth: this handler runs on the UI-thread dispatcher. A malformed frame of
        // any kind must never abort the rest of the mux stream (which would freeze live AI text).
        try
        {
            OnScopeMuxFrameCore(frame);
        }
        catch (Exception ex)
        {
            Logging.Get<MainViewModel>().LogWarning(ex, "OnScopeMuxFrame failed; frame={Kind}", frame?.GetType().Name);
        }
    }

    private void OnScopeMuxFrameCore(MuxFrame frame)
    {
        // P1-14 step 3: per-session frames are only processed if the session belongs to this
        // window's tree; approval/question only if it's the window's focused session. This keeps
        // a secondary window from folding another window's turns into its own chat.
        switch (frame)
        {
            case SessionEventFrame e:
                // Accept the event when the session is in this window's tree OR it is the
                // currently focused session. The tree-refresh window (Workspaces.Clear → refill)
                // would otherwise drop live frames for the focused session, leaving a blank chat.
                if (IsSessionInTree(e.SessionId) || e.SessionId == SelectedSessionId)
                {
                    // Pass the frame's render intent through: the host puts a mutation tool's
                    // view NEXT TO the event (top-level `view`), and SessionFold needs it to
                    // derive the produced-files bar. Dropping it (the old behavior) starved
                    // the fold — the bar never showed a file even though the host sent one.
                    AppendEvent(e.SessionId, e.Event, e.View);
                }
                break;
            case SessionProjectionFrame p:
                if (IsSessionInTree(p.SessionId))
                {
                    _projections.Apply(p);
                    if (p.SessionId == SelectedSessionId) RefreshSessionStats(); // P2-7.
                }
                break;
            case SessionJobsFrame j:
                if (IsSessionInTree(j.SessionId)) SyncJobs(j.SessionId, j.Jobs);
                break;
            case SessionQueueFrame q:
                if (IsSessionInTree(q.SessionId)) SyncQueue(q.Items);
                break;
            case ApprovalRequestedFrame a:
                if (a.SessionId != SelectedSessionId) break;
                _interactions.Open(frame);
                OnApprovalRequested(a);
                break;
            case ApprovalResolvedFrame:
                // _interactions is per-window; settling a foreign session is a safe no-op.
                _interactions.Settle(frame);
                SyncFoldToUi();
                break;
            case QuestionRequestedFrame q:
                if (q.SessionId != SelectedSessionId) break;
                _interactions.Open(frame);
                OnQuestionRequested(q);
                break;
            case QuestionResolvedFrame:
                _interactions.Settle(frame);
                SyncFoldToUi();
                break;
        }
    }

    private void OnApprovalRequested(ApprovalRequestedFrame a)
    {
        // RpcId is backfilled from the enclosing server-request envelope; if it is
        // missing the approve/reject respond() has no correlation id, so skip the
        // pending UI rather than crash on a null-forcing dereference.
        if (a.RpcId is null)
        {
            _fold.Rows.Add(new SessionFold.Row("error",
                $"⚠ 工具「{a.ToolName}」请求许可，但缺少关联 rpcId，无法回复（已忽略）。"));
            SyncFoldToUi();
            return;
        }
        _fold.Rows.Add(new SessionFold.Row("system",
            $"⚠ 工具「{a.ToolName}」请求许可：{(string.IsNullOrEmpty(a.Reason) ? "批准或拒绝？" : a.Reason)}"));
        PendingApproval = new ApprovalRequest(a, a.RpcId);
        SetSessionWaiting(a.SessionId, true); // P1-2: amber dot while awaiting an interaction.
        SyncFoldToUi();

        // J1: when the window may be in the tray, surface an approval request as a tray
        // balloon so the user notices even without focus on the window. Dedupe by ApprovalId.
        if (a.ApprovalId != _lastApprovalBalloonId)
        {
            _lastApprovalBalloonId = a.ApprovalId;
            string title = Loc.Get("Tray.ApprovalTitle");
            string body = string.IsNullOrEmpty(a.Reason)
                ? string.Format(Loc.Get("Tray.ApprovalBodyTool"), a.ToolName)
                : string.Format(Loc.Get("Tray.ApprovalBodyReason"), a.ToolName, a.Reason);
            App.Tray?.NotifyBalloon(title, body);
        }
    }

    private string? _lastApprovalBalloonId;

    private void OnQuestionRequested(QuestionRequestedFrame q)
    {
        // Same guard as approvals: a question without a rpcId cannot be answered.
        if (q.RpcId is null)
        {
            _fold.Rows.Add(new SessionFold.Row("error",
                "⚠ 收到用户提问，但缺少关联 rpcId，无法回复（已忽略）。"));
            SyncFoldToUi();
            return;
        }
        PendingQuestion = q.RpcId;
        PendingQuestionSession = q.SessionId;
        PendingQuestions.Clear();
        foreach (var item in q.Questions)
        {
            PendingQuestions.Add(new QuestionUi(item));
        }
        _fold.Rows.Add(new SessionFold.Row("system", "⚠ 需要回答问题（见底部问题面板）"));
        SetSessionWaiting(q.SessionId, true); // P1-2: amber dot while awaiting an interaction.
        SyncFoldToUi();
    }

    [RelayCommand]
    private async Task SubmitAnswersAsync()
    {
        if (PendingQuestion is null || PendingQuestionSession is null) return;
        RpcId rpcId = PendingQuestion;   // RpcId is a reference record; null-checked above
        string sessionId = PendingQuestionSession;

        var answers = PendingQuestions
            .Where(x =>
            {
                // plan-review has no options; an empty approval submits as "approved" unless custom text was typed.
                if (x.IsPlanReview) return x.Approve is not null || !string.IsNullOrWhiteSpace(x.Custom);
                return x.SelectedLabels.Any() || !string.IsNullOrWhiteSpace(x.Custom);
            })
            .Select(x => new QuestionAnswer(
                x.Id,
                x.IsPlanReview && !x.SelectedLabels.Any()
                    ? (x.Approve is not null ? new[] { x.Approve } : Array.Empty<string>())
                    : x.SelectedLabels.ToArray(),
                string.IsNullOrWhiteSpace(x.Custom) ? null : x.Custom.Trim()))
            .ToList();

        try
        {
            await _interactions.RespondQuestions(rpcId, sessionId, answers);
            PendingQuestion = null;
            PendingQuestionSession = null;
            PendingQuestions.Clear();
            SetSessionWaiting(sessionId, false); // P1-2: amber dot clears once answers are posted.
        }
        catch (Exception ex)
        {
            _fold.Rows.Add(new SessionFold.Row("error", $"回答问题失败：{ex.Message}"));
            NotifyError($"回答问题失败：{ex.Message}");
            SyncFoldToUi();
        }
    }

    private void OnScopeHostFrame(HostFrame frame)
    {
        switch (frame)
        {
            case HostSessionStatusFrame s:
                // P1-2/P1-3: flip the session's running flag live in the tree (idempotent per
                // window — each window only touches its own tree nodes). Also trim the fold on
                // turn end. Works for both main and secondary windows.
                SetSessionRunning(s.SessionId, s.Running);
                if (!s.Running) SyncFoldToUi();
                break;
            case HostSessionAddedFrame:
                if (IsMainWindow) _ = RefreshSessionsAsync(); // main window owns the tree.
                break;
            case HostSessionRemovedFrame r:
                if (IsMainWindow) RemoveSessionNode(r.SessionId);
                // B2: also drop the removed session's projection cells (previously left in the
                // store until generation teardown → per-session memory leak on churn).
                _projections.RemoveSession(r.SessionId);
                break;
            case HostWorkspaceChangedFrame w:
                // P1-14 step 3: only the main window mutates the shared workspace tree; a
                // secondary window ignores workspace changes to avoid racing the main tree.
                if (IsMainWindow) UpsertWorkspaceNode(w.Workspace);
                break;
            case HostWorkspaceRemovedFrame r:
                if (IsMainWindow) RemoveWorkspaceNode(r.WorkspaceId);
                break;
            case HostWorkspaceOrderChangedFrame o:
                if (IsMainWindow) ReorderWorkspaceNodes(o.WorkspaceIds);
                break;
            case HostArchivedSessionsChangedFrame a:
                if (IsMainWindow) _ = RefreshSessionsAsync();
                break;
        }
    }

    /// <summary>Flip a session row's running flag in the tree (P1-3).</summary>
    private void SetSessionRunning(string sessionId, bool running)
    {
        foreach (var ws in Workspaces)
        {
            foreach (var s in ws.Sessions)
            {
                if (s.Id == sessionId)
                {
                    s.Running = running;
                    return;
                }
            }
        }
    }

    /// <summary>Remove a session row from whatever workspace node holds it (P1-4).</summary>
    private void RemoveSessionNode(string sessionId)
    {
        foreach (var ws in Workspaces)
        {
            var target = ws.Sessions.FirstOrDefault(s => s.Id == sessionId);
            if (target is not null)
            {
                ws.Sessions.Remove(target);
                return;
            }
        }
    }

    /// <summary>
    /// Insert-or-replace a workspace node from a <c>host/workspace-changed</c> snapshot (P1-3).
    /// The frame payload is a full <c>WorkspaceView</c>; we preserve existing session rows where
    /// possible and only re-fetch sessions when membership changed.
    /// </summary>
    private void UpsertWorkspaceNode(object rawWorkspace)
    {
        WorkspaceView? view = null;
        try
        {
            view = rawWorkspace switch
            {
                WorkspaceView v => v,
                JsonElement el => el.Deserialize<WorkspaceView>(JsonEnvelopeCodec.Options),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            Logging.Get<MainViewModel>().LogWarning(ex, "workspace-changed payload unparseable; falling back to full refresh");
        }

        if (view is null)
        {
            _ = RefreshWorkspacesAsync();
            return;
        }

        var existing = Workspaces.FirstOrDefault(n => n.WorkspaceId == view.WorkspaceId);
        if (existing is null)
        {
            // New workspace: add a node and reconcile its membership via a light session refresh.
            Workspaces.Add(new WorkspaceNode(view.WorkspaceId, view.Title, view.Path));
            _ = RefreshSessionsAsync();
            return;
        }

        // Existing workspace: leave the node in place (keeps selection/expansion); the order
        // and membership are reconciled below.
        _ = RefreshSessionsAsync();
    }

    /// <summary>Remove a workspace node from the tree (P1-3/P1-4).</summary>
    private void RemoveWorkspaceNode(string workspaceId)
    {
        var node = Workspaces.FirstOrDefault(n => n.WorkspaceId == workspaceId);
        if (node is not null)
        {
            Workspaces.Remove(node);
        }
    }

    /// <summary>Reorder workspace nodes to match the host's durable registry order (P1-3).</summary>
    private void ReorderWorkspaceNodes(string[] workspaceIds)
    {
        // Move each workspace node to match the incoming id order; unknown ids are left at the end.
        int i = 0;
        foreach (var id in workspaceIds)
        {
            var node = Workspaces.FirstOrDefault(n => n.WorkspaceId == id);
            if (node is not null)
            {
                Workspaces.Move(Workspaces.IndexOf(node), i++);
            }
        }
    }

    /// <summary>
    /// Move a workspace before a target (P2-1). Parameters come from the drag-drop handler:
    /// sourceWorkspaceId + targetWorkspaceId (null target = move to end).
    /// </summary>
    [RelayCommand]
    private async Task ReorderWorkspaceAsync(ReorderTarget? p)
    {
        if (p is null || string.IsNullOrEmpty(p.SourceId)) return;
        try
        {
            await _sessions.InsertWorkspaceBefore(p.SourceId, p.TargetId);
            // The host emits HostWorkspaceOrderChangedFrame (P1-3) which reconciles the tree.
        }
        catch (Exception ex)
        {
            NotifyError($"移动工作区失败：{ex.Message}");
            _ = RefreshWorkspacesAsync();
        }
    }

    /// <summary>
    /// Move a session before a target session within a workspace (P2-1).
    /// </summary>
    [RelayCommand]
    private async Task ReorderSessionAsync(ReorderTarget? p)
    {
        if (p is null || string.IsNullOrEmpty(p.SourceId) || string.IsNullOrEmpty(p.WorkspaceId)) return;
        try
        {
            await _sessions.InsertSessionBefore(p.WorkspaceId, p.SourceId, p.TargetId);
            _ = RefreshSessionsAsync();
        }
        catch (Exception ex)
        {
            NotifyError($"移动会话失败：{ex.Message}");
        }
    }

    /// <summary>Parameters for a workspace/session reorder (P2-1 drag-drop).</summary>
    public sealed record ReorderTarget(string? SourceId, string? TargetId, string? WorkspaceId = null);

    private void AppendEvent(string sessionId, object? rawEvent, object? rawView = null)
    {

        // The event slot is a JsonElement (STJ materializes `object` as JSON); fold it.
        if (rawEvent is not JsonElement el) return;
        // A single malformed event must NOT tear down the mux/host receive loop — fold it
        // defensively and surface the failure as a row so the stream keeps flowing for the
        // rest of the session.
        string eventType = el.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? ""
            : "";

        // P0-2: turn boundaries refine the composer badge, but only when the event belongs
        // to the currently selected session (mux aggregates every session).
        if (sessionId == SelectedSessionId)
        {
            switch (eventType)
            {
                case "turn/start":
                    ComposerMode = ComposerMode.Running;
                    break;
                case "turn/end":
                    ComposerMode = ComposerMode.Idle;
                    IsStreaming = false;
                    // 强制冲刷节流，让 turn 终止时的最终 chunk 立刻可见（不等待 250ms 防抖尾）。
                    _uiThrottle?.Flush();
                    break;
                // 呼吸指示：assistant chunks stream; a completed assistant message or a
                // user message pauses it until the next chunk arrives.
                case "assistant/chunk":
                    IsStreaming = true;
                    break;
                case "assistant/message":
                case "user/message":
                    IsStreaming = false;
                    // 流式暂停/结束：强制冲刷节流，让最终状态立即呈现（不等待防抖尾）。
                    _uiThrottle?.Flush();
                    break;
            }
        }

        // CRITICAL: only fold + re-render the transcript for the currently focused session.
        // The shared `_fold` carries one session at a time; events for other in-tree sessions
        // must not pollute the UI. The tree/host frames already filter by IsSessionInTree for
        // the secondary list updates; here we gate on SelectedSessionId so a live frame for a
        // different session never mixes into this window's chat.
        if (sessionId != SelectedSessionId) return;

        try
        {
            // The view slot is materialized by STJ as JSON exactly like the event slot.
            _fold.Fold(el, rawView as JsonElement?);
        }
        catch (Exception ex)
        {
            Logging.Get<MainViewModel>().LogWarning(ex, "AppendEvent fold failed; dropping event");
            _fold.Rows.Add(new SessionFold.Row("error", $"事件处理失败（type={eventType}）：{ex.Message}"));
        }

        // E10: owner events invalidate cached surfaces. A settings/credentials/adapters
        // update should be re-pulled so the settings page, credential badges, and model
        // list reflect the change without a manual refresh.
        switch (eventType)
        {
            case "settings/document-updated":
                _ = LoadSettingsAsync();
                break;
            case "credentials/updated":
                _ = LoadCredentialsAsync();
                break;
            case "llm/adapters-updated":
                _ = LoadModelsAsync();
                break;
        }
        // 流式节流：合并高频 chunk 的 Transcript 同步，避免 UI 线程被逐 chunk 全量重建阻塞。
        // Flush() 在 turn/end / assistant/message 等边界强制冲刷，确保最终态立即可见。
        _uiThrottle?.Schedule();
    }

    /// <summary>Reconcile the fold's surface rows onto the UI transcript collection.</summary>
    private void SyncFoldToUi()
    {
        // P0-1: commit any in-flight streamed text (one O(n) copy) before we read the rows to
        // render them. Without this the streaming buffer would never reach the UI.
        _fold.MaterializePendingRow();

        // P2-15: when the fold is very long, only render a trailing window to keep the
        // UI collection bounded. The full history still lives in the fold itself.
        int start = _fold.Rows.Count > MaxTranscriptRenderLines
            ? _fold.Rows.Count - MaxTranscriptRenderLines
            : 0;
        int renderCount = _fold.Rows.Count - start;

        // Hybrid reconcile (freeze-proof + flicker-free):
        //  • If the visible window's structure (count + per-row role/messageId/time) is unchanged
        //    from what's already in Transcript, we do cheap in-place property updates. This avoids
        //    rebuilding ChatEntry controls (and re-running Markdig in AssistantMessageControl) on
        //    every streaming flush — no flicker, no per-row cost.
        //  • Otherwise we build a fresh snapshot list and swap Transcript in ONE assignment, which
        //    raises a single notification instead of one CollectionChanged per row. For a 600-row
        //    window that's 600x fewer layout invalidations — this is what stops a large session from
        //    freezing the UI thread on open/scroll. With UI virtualization only the visible viewport
        //    realizes its AssistantMessageControl, so the single layout pass stays cheap.
        // M1/自绘：assistant 行若紧跟前一非 assistant 行，则为新 turn（头像仅首条显示）。
        bool structureMatches =
            Transcript.Count == renderCount &&
            SameStructure(0, start, renderCount);

        if (structureMatches)
        {
            // In-place updates only — no collection notification, no control rebuild.
            bool prevWasAssistant = start > 0 && _fold.Rows[start - 1].Role == "assistant";
            for (int i = 0; i < renderCount; i++)
            {
                var row = _fold.Rows[start + i];
                bool turnStart = row.Role == "assistant" && !prevWasAssistant;
                var entry = Transcript[i];
                // P1-6: only assign when the value actually changed. Assigning unconditionally
                // raises PropertyChanged on every row of the window, and each of those triggers
                // AssistantMessageControl.Rebuild → a full Markdig re-parse — so a 600-row window
                // re-parsed 600 messages every 250ms even though only the last one changed.
                if (!ReferenceEquals(entry.Text, row.Text)) entry.Text = row.Text;
                if (!ReferenceEquals(entry.Reasoning, row.Reasoning)) entry.Reasoning = row.Reasoning;
                if (!ReferenceEquals(entry.Tool, row.Tool)) entry.Tool = row.Tool;
                if (entry.IsTurnStart != turnStart) entry.IsTurnStart = turnStart;
                prevWasAssistant = row.Role == "assistant";
            }
        }
        else
        {
            var snapshot = new System.Collections.Generic.List<ChatEntry>(renderCount);
            bool prevWasAssistant = start > 0 && _fold.Rows[start - 1].Role == "assistant";
            for (int i = 0; i < renderCount; i++)
            {
                var row = _fold.Rows[start + i];
                bool turnStart = row.Role == "assistant" && !prevWasAssistant;
                snapshot.Add(new ChatEntry(row.Role, row.Text, row.Reasoning, row.Tool, row.MessageId, row.Time)
                { IsTurnStart = turnStart });
                prevWasAssistant = row.Role == "assistant";
            }
            Transcript.ReplaceAll(snapshot);
        }

        // Sync the todo whole-list snapshot.
        Todos.Clear();
        foreach (var todo in _fold.Todos)
        {
            Todos.Add(todo);
        }

        // H3 (P2-5): surface the current turn's produced files.
        // Same self-assignment trap as the trajectory below: `_fold.Deliverables` returns the
        // same live List instance every call, so a plain assignment never raises PropertyChanged
        // and the produced-files bar never gained its file buttons. Publish a fresh array only
        // when the fold actually mutated the list (version bumped) — zero allocation otherwise.
        if (_fold.DeliverablesVersion != _publishedDeliverablesVersion)
        {
            _publishedDeliverablesVersion = _fold.DeliverablesVersion;
            Deliverables = _fold.Deliverables.ToArray();
        }
        // H1 (P2-6): surface the trajectory timeline.
        // CRITICAL (the "trajectory is blank" bug): `_fold.Trajectory` returns the SAME live List
        // instance on every call, so `Trajectory = _fold.Trajectory` is a self-assignment after
        // the first sync. The [ObservableProperty] setter compares by reference, sees no change,
        // and never raises PropertyChanged — the control therefore kept its initial empty snapshot
        // forever, which is exactly why the WPF trajectory showed nothing while the native-web one
        // was fully populated. Publishing a NEW array only when the fold's trajectory actually
        // mutated (version bumped) fixes the notification while keeping the copy off the hot path
        // (zero allocation when nothing changed).
        if (_fold.TrajectoryVersion != _publishedTrajectoryVersion)
        {
            _publishedTrajectoryVersion = _fold.TrajectoryVersion;
            Trajectory = _fold.Trajectory.ToArray();
        }
    }

    /// <summary>
    /// Returns true when the <paramref name="count"/> fold rows starting at <paramref name="start"/>
    /// have the same role/messageId/time signature as the current Transcript entries at the same
    /// indices — i.e. only their text/reasoning/tool content may have changed (safe for in-place
    /// update). The null→real MessageId promotion of an optimistic user row is treated as a match
    /// so it doesn't force a full rebuild (avoids user-bubble flicker, problem A).
    /// </summary>
    private bool SameStructure(int transcriptOffset, int foldStart, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var entry = Transcript[transcriptOffset + i];
            var row = _fold.Rows[foldStart + i];
            bool identityChanged = entry.Role != row.Role
                                    || (entry.MessageId != row.MessageId && !(entry.MessageId is null && row.MessageId is not null))
                                    || entry.Time != row.Time;
            if (identityChanged) return false;
        }
        return true;
    }

    /// <summary>
    /// Replace the Jobs collection from the <c>session/jobs</c> snapshot. The frame is a
    /// whole snapshot (last-wins), so a full swap reflects status transitions (running →
    /// completed) and removals, not just additions.
    /// </summary>
    private void SyncJobs(string sessionId, JobInfo[] incoming)
    {
        Jobs.Clear();
        bool anyRunning = false;
        foreach (var job in incoming)
        {
            Jobs.Add(new JobUi(job)); // P1-12: wrap for a localized phase label.
            // P1-2: a job that is queued/running keeps the session's green dot lit.
            if (job.Status is "queued" or "running" or "stopping")
            {
                anyRunning = true;
            }
        }
        SetSessionJobRunning(sessionId, anyRunning);
    }

    /// <summary>Replace the queue from the <c>session/queue</c> snapshot (whole, last-wins).</summary>
    private void SyncQueue(QueuedInboxItem[] incoming)
    {
        QueueItems.Clear();
        foreach (var item in incoming)
        {
            QueueItems.Add(item);
        }
    }

    // ---- H7: session.updateQueue (edit / remove / steer) ----

    /// <summary>
    /// Remove a pending queue item (H7). Calls <c>session.updateQueue { action: remove }</c>.
    /// </summary>
    [RelayCommand]
    private async Task RemoveQueueItem(QueuedInboxItem? item)
    {
        if (item is null || SelectedSessionId is not string sid) return;
        try
        {
            await _sessions.UpdateQueue(sid, item.Id, new QueueActionRemove());
        }
        catch (Exception ex)
        {
            NotifyError(string.Format(Loc.Get("Queue.UpdateFailed"), ex.Message));
        }
    }

    /// <summary>
    /// Strict-steer a pending queue item into the current turn (H7). Calls
    /// <c>session.updateQueue { action: steer }</c>.
    /// </summary>
    [RelayCommand]
    private async Task SteerQueueItem(QueuedInboxItem? item)
    {
        if (item is null || SelectedSessionId is not string sid) return;
        try
        {
            await _sessions.UpdateQueue(sid, item.Id, new QueueActionSteer());
        }
        catch (Exception ex)
        {
            NotifyError(string.Format(Loc.Get("Queue.UpdateFailed"), ex.Message));
        }
    }

    /// <summary>
    /// Edit a pending queue item's text (H7). Prompts for new content then calls
    /// <c>session.updateQueue { action: edit, content: [{type:'text', text}] }</c>.
    /// </summary>
    [RelayCommand]
    private async Task EditQueueItem(QueuedInboxItem? item)
    {
        if (item is null || SelectedSessionId is not string sid) return;
        var dlg = new InputDialog(Loc.Get("Queue.EditTitle"), Loc.Get("Queue.EditPrompt"), item.TextPreview);
        var owner = System.Windows.Application.Current.MainWindow;
        if (owner is not null)
        {
            dlg.Owner = owner;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        if (dlg.ShowDialog() != true) return;
        string text = dlg.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var content = new object[] { new { type = "text", text } };
            await _sessions.UpdateQueue(sid, item.Id, new QueueActionEdit { Content = content });
        }
        catch (Exception ex)
        {
            NotifyError(string.Format(Loc.Get("Queue.UpdateFailed"), ex.Message));
        }
    }
}
