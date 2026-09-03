using System.Collections.ObjectModel;
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
using Dsh.Contract.Methods;
using Dsh.Contract.Frames;
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

    /// <summary>
    /// Update a session row's job-running flag in the sidebar tree (P1-2). Flips the green dot live.
    /// </summary>
    private void SetSessionJobRunning(string sessionId, bool running)
    {
        foreach (var ws in Workspaces)
        {
            foreach (var s in ws.Sessions)
            {
                if (s.Id == sessionId)
                {
                    s.JobRunning = running;
                    return;
                }
            }
        }
    }

    /// <summary>Load a session's history tail and fold it into the chat surface.</summary>
    private async Task LoadHistoryAsync(string sessionId)
    {
        Transcript.Clear();
        // P2-5/P2-6: reset per-session fold state (trajectory, tool-view cache, deliverables).
        _fold.Reset();
        // Force a fresh trajectory publish for this session (guards the case where a new fold's
        // version counter happens to match what we last published).
        _publishedTrajectoryVersion = -1;
        _publishedDeliverablesVersion = -1;
        _fold.Rows.Clear();
        // P2: reset pagination state for the freshly-loaded session.
        _historyCursor = null;
        _historyHasMore = false;
        _historyLoadingOlder = false;
        // Suppress AutoScrollBehavior's per-batch scrolling while the tail page renders, so the
        // view doesn't jump repeatedly; it re-pins to the latest message once batches finish.
        IsLoadingHistory = true;
        try
        {
            var history = await _sessions.GetHistory(sessionId, maxMessages: 500);
            // Stale-session guard: ignore history that arrived after the user switched away.
            if (SelectedSessionId != sessionId) return;

            int folded = 0;
            var seenTypes = new System.Collections.Generic.HashSet<string>();
            // J7: cache the raw events of this successful page so the transcript can be replayed
            // offline if the next load of this session hits a network failure. Run on the
            // thread pool to keep the synchronous File.WriteAllText off the UI thread (a
            // multi-MB history page would otherwise block the message pump during render).
            var cachedEvents = System.Linq.Enumerable.Select(history.Events, e => e.Event).ToArray();
            _ = System.Threading.Tasks.Task.Run(() => OfflineCache.SaveHistory(sessionId, cachedEvents));

            // E10 (2026-08-24): fold N events + ToArray on the thread pool so the UI thread is
            // not blocked while the fold dispatches each event to HandleUserMessage /
            // HandleAssistantMessage / HandleAssistantChunk / etc. ConnectionScope already
            // marshals the live MuxFrame.ReadDownlink calls to the UI dispatcher, so a
            // Task.Run fold here never races with live events (it's synchronous on the worker
            // and the UI thread cannot be inside a dispatcher pump while we're awaiting).
            await System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var entry in history.Events)
                {
                    string type = entry.Event.ValueKind == System.Text.Json.JsonValueKind.Object
                        && entry.Event.TryGetProperty("type", out var t)
                        && t.ValueKind == System.Text.Json.JsonValueKind.String
                        ? t.GetString() ?? ""
                        : "(non-object)";
                    seenTypes.Add(type);
                    // Pass the entry's render intent: the host pairs every tool event with the
                    // view its presenter produced (same slot as the mux frame's `view`), and
                    // the produced-files bar needs it to list mutation-touched files. Dropping
                    // it (the old behavior) meant a reloaded session lost its file buttons.
                    if (_fold.Fold(entry.Event, entry.View)) folded++;
                }
            });

            // C17/D3: seed the projection store from the history tail page's projections block.
            // The host carries the current value of every registered projection unit
            // (sessionStats / tokenUsage / contextPressure / plan / permissions …) on the tail
            // page (SessionProjectionsBlock: { asOfSeq, values }). Without this, a cold-switched
            // session has no stats/token baseline until the host pushes a change frame — which
            // is why the token line stayed empty. Seed under the same higher-seq-wins rule the
            // live session/projection frames use.
            if (history.Projections is { ValueKind: System.Text.Json.JsonValueKind.Object } proj
                && proj.TryGetProperty("values", out var values) && values.ValueKind == System.Text.Json.JsonValueKind.Object
                && proj.TryGetProperty("asOfSeq", out var asOfSeqProp) && asOfSeqProp.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                long asOfSeq = asOfSeqProp.GetInt64();
                foreach (var kv in values.EnumerateObject())
                {
                    _projections.ApplyValue(sessionId, kv.Name, kv.Value, asOfSeq);
                }
                RefreshSessionStats();
            }
            // D1: 分批渲染 fold 结果，避免一次性构建上千行 ChatEntry 卡死 UI 线程。
            // 分批期间 PauseScrollToEnd=true（IsLoadingHistory）抑制逐批滚动；完成后复位
            // 使 AutoScrollBehavior 一次性定位到底部（最新消息），消除"逐批滚动"动画感。
            _fold.Compact(); // 移除历史折叠后仍为空的消息行（避免空气泡）
            await RenderFoldInBatchesAsync();
            IsLoadingHistory = false;

            // P2: 记录 tail 页的分页游标（最早事件 seq）供 loadOlder 向上翻页。
            SetCursorFromPage(history);

            if (history.Events.Length == 0)
            {
                _fold.Rows.Add(new SessionFold.Row("error",
                    Loc.Format("Fold.SessionEmpty", history.HasMore)));
                SyncFoldToUi();
            }
            else if (Transcript.Count == 0)
            {
                var unknownOnly = new System.Collections.Generic.HashSet<string>();
                foreach (var ty in seenTypes)
                {
                    if (ty is "permission/preset" or "sandbox/mode"
                        or "approval/policy" or "session/end-seed"
                        or "" or "(non-object)")
                        continue;
                    unknownOnly.Add(ty);
                }
                string suffix = unknownOnly.Count > 0
                    ? Loc.Format("Fold.UnknownTypes", string.Join(", ", unknownOnly), string.Join(", ", seenTypes))
                    : Loc.Format("Fold.AllTypes", string.Join(", ", seenTypes));
                _fold.Rows.Add(new SessionFold.Row("error",
                    Loc.Format("Fold.TranscriptEmpty", history.Events.Length, folded, suffix)));
                SyncFoldToUi();
            }
        }
        catch (Exception ex)
        {
            // J7: on a network-level failure (host unreachable), replay the cached history page
            // for this session instead of showing a dead error row — mirrors the sidebar's
            // offline browse. Other exceptions (business errors) still surface the raw message.
            if (ex is System.Net.Http.HttpRequestException)
            {
                var cached = OfflineCache.TryLoadHistory(sessionId);
                if (cached is not null && cached.Events.Length > 0)
                {
                    foreach (var ev in cached.Events)
                    {
                        _fold.Fold(ev);
                    }
                    _fold.Compact(); // 移除离线回放后仍为空的消息行
                    await RenderFoldInBatchesAsync();
                    // Show a subtle offline marker so it's clear the view is stale/cached.
                    _fold.Rows.Add(new SessionFold.Row("system", Loc.Get("Fold.OfflineReplay")));
                    SyncFoldToUi();
                }
                else
                {
                    _fold.Rows.Add(new SessionFold.Row("error", Loc.Format("Fold.LoadHistoryFailed", ex.Message)));
                    SyncFoldToUi();
                }
            }
            else
            {
                _fold.Rows.Add(new SessionFold.Row("error", Loc.Format("Fold.LoadHistoryFailed", ex.Message)));
                SyncFoldToUi();
            }
        }
        finally
        {
            // Safe reset: if the load was aborted (stale guard / exception) before resuming,
            // still clear the pause flag so future streaming scrolls normally. When it was
            // already reset above, this is a no-op (no property change, no extra scroll).
            IsLoadingHistory = false;
        }
    }

    /// <summary>
    /// P2: record the pagination cursor (earliest event seq) and hasMore from a history page.
    /// The cursor is the smallest <c>seq</c> seen in the page's events; loadOlder requests
    /// <c>seq &lt; cursor</c> to walk further back. An empty page → hasMore=false (no more old rows).
    /// </summary>
    private void SetCursorFromPage(Dsh.Contract.Methods.SessionHistoryPage page)
    {
        _historyHasMore = page.HasMore;
        if (page.Events.Length == 0)
        {
            _historyCursor = null;
            _historyHasMore = false;
            return;
        }
        long? min = null;
        foreach (var entry in page.Events)
        {
            if (entry.Event.ValueKind == System.Text.Json.JsonValueKind.Object
                && entry.Event.TryGetProperty("seq", out var s)
                && s.ValueKind == System.Text.Json.JsonValueKind.Number
                && s.TryGetInt64(out var seq))
            {
                min = min is null || seq < min ? seq : min;
            }
        }
        _historyCursor = min;
    }

    /// <summary>
    /// P2: load one older page (events strictly older than <see cref="_historyCursor"/>) on
    /// up-scroll, folding them into a fresh fold (they are frozen history — no stream updates
    /// target them) and inserting the resulting rows at the head of both <see cref="_fold.Rows"/>
    /// and <see cref="Transcript"/>. The original first transcript item is re-scrolled into view
    /// (via <see cref="ScrollToAnchorRequested"/>) so the user's scroll position is preserved.
    /// </summary>
    [RelayCommand]
    private async Task LoadOlderHistoryAsync()
    {
        if (_historyLoadingOlder || IsLoadingHistory || !_historyHasMore || _historyCursor is null) return;
        string? sessionId = SelectedSessionId;
        if (sessionId is null) return;
        _historyLoadingOlder = true;
        // 与 tail 加载一致：loadOlder 头插期间置 IsLoadingHistory=true，让
        // AutoScrollBehavior 的 PauseScrollToEnd 抑制逐条 Insert 触发的 ScrollToEnd，
        // 消除"滚到底"与锚点 ScrollIntoView(anchor) 的拉锯（假死根因）。
        IsLoadingHistory = true;
        try
        {
            var page = await _sessions.GetHistory(sessionId, maxMessages: 500, beforeSeq: _historyCursor);
            // Stale-session guard.
            if (SelectedSessionId != sessionId) return;
            if (page.Events.Length == 0)
            {
                _historyHasMore = false;
                return;
            }

            // Fold the older (frozen) events into an isolated fold — never Append into the live
            // fold, whose rows are ordered by seq and whose tail is the stream's AppendChunk target.
            var olderFold = new SessionFold();
            foreach (var entry in page.Events)
            {
                olderFold.Fold(entry.Event);
            }
            olderFold.Compact(); // 移除分页旧消息中最终为空的行
            var olderRows = olderFold.Rows;
            if (olderRows.Count == 0)
            {
                SetCursorFromPage(page);
                return;
            }

            // Anchor = the transcript item that was first before this insertion; the view scrolls
            // back to it after the head-insert to keep the user's position stable.
            object? anchor = Transcript.Count > 0 ? Transcript[0] : null;

            // Build the full prepend (older rows first, then the rows already on screen) into a
            // local list, then swap it into Transcript in ONE assignment. This raises a single
            // PropertyChanged instead of one CollectionChanged per Insert(0,…) — the previous loop
            // (500 rows → 500 layout passes in one synchronous pass) was itself a 假死 cause. With
            // UI virtualization the realized cost is only the visible window regardless, but the
            // single notification also kills the repeated index-shift work. AutoScroll is already
            // paused (IsLoadingHistory=true above), so the swap doesn't fight the anchor re-scroll.
            bool prevWasAssistant = false;
            var prepended = new System.Collections.Generic.List<ChatEntry>(olderRows.Count + Transcript.Count);
            for (int i = 0; i < olderRows.Count; i++)
            {
                var row = olderRows[i];
                bool turnStart = row.Role == "assistant" && !prevWasAssistant;
                prepended.Add(new ChatEntry(row.Role, row.Text, row.Reasoning, row.Tool, row.MessageId, row.Time)
                { IsTurnStart = turnStart });
                prevWasAssistant = row.Role == "assistant";
            }
            for (int i = 0; i < Transcript.Count; i++)
            {
                prepended.Add(Transcript[i]);
            }
            Transcript.ReplaceAll(prepended);
            await Task.Yield(); // let the single layout pass settle before the caller continues.

            // Prepend older rows to the fold (Rows[^1] remains the live tail → AppendChunk intact).
            var combined = new List<SessionFold.Row>(olderRows.Count + _fold.Rows.Count);
            combined.AddRange(olderRows);
            combined.AddRange(_fold.Rows);
            _fold.Rows.Clear();
            _fold.Rows.AddRange(combined);

            SetCursorFromPage(page);

            if (anchor is not null)
            {
                ScrollToAnchorRequested?.Invoke(anchor);
            }
        }
        catch (Exception ex)
        {
            _fold.Rows.Add(new SessionFold.Row("error", Loc.Format("Fold.LoadOlderFailed", ex.Message)));
            SyncFoldToUi();
        }
        finally
        {
            _historyLoadingOlder = false;
            IsLoadingHistory = false;
        }
    }

    /// <summary>
    /// D1: 分批把 fold 的 rows 尾窗同步到 <see cref="Transcript"/>，每批让出 UI 线程，
    /// 避免加载大量历史时一次性构建上千行 <see cref="ChatEntry"/>（每个都做 Markdig 解析 +
    /// 视觉树构建）导致 UI 卡死。滚动到底由 <see cref="AutoScrollBehavior"/> 对每批 Add 的
    /// <c>ScrollToEnd</c> 保证——最新消息停靠底部。与 <see cref="SyncFoldToUi"/> 不同的是：
    /// 本方法专用于"加载历史"（<see cref="Transcript"/> 初始为空，直接逐行 Add），不复用
    /// 流式路径的身份比较/原地更新逻辑（那会干扰批次 yield 的正确性）。
    /// </summary>
    private async Task RenderFoldInBatchesAsync()
    {
        // E9 (2026-08-24): build ALL ChatEntry instances into a local list first, then add them
        // in one contiguous dispatcher pass. ChatEntry construction is cheap (it only carries
        // data); the expensive work — AssistantMessageControl.Rebuild — happens only when the
        // ListBox realizes a visible item, which happens once per layout, not once per Add.
        // Previously we awaited Task.Yield() every 250 adds, which forced a layout (and thus a
        // re-realize of the viewport) each batch; for a multi-thousand-row history that added
        // up to many visible-item Markdig re-parses and felt like "almost never loads".
        int start = _fold.Rows.Count > MaxTranscriptRenderLines
            ? _fold.Rows.Count - MaxTranscriptRenderLines
            : 0;
        int renderCount = _fold.Rows.Count - start;

        var batch = new System.Collections.Generic.List<ChatEntry>(renderCount);
        bool prevWasAssistant = start > 0 && _fold.Rows[start - 1].Role == "assistant";
        for (int i = 0; i < renderCount; i++)
        {
            var row = _fold.Rows[start + i];
            bool turnStart = row.Role == "assistant" && !prevWasAssistant;
            batch.Add(new ChatEntry(row.Role, row.Text, row.Reasoning, row.Tool, row.MessageId, row.Time)
            { IsTurnStart = turnStart });
            prevWasAssistant = row.Role == "assistant";
        }

        // Swap the whole window in ONE call: a single Reset notification instead of N
        // CollectionChanged events. The virtualized ListBox realizes only the visible viewport, so
        // Markdig (inside AssistantMessageControl.Rebuild) runs only for on-screen rows — the
        // multi-thousand-row history no longer blocks the UI thread on open/scroll. ReplaceAll
        // mutates the SAME collection instance, so it never raises the VM's PropertyChanged off the
        // UI thread (which would deadlock WPF bindings during the background history fold).
        Transcript.ReplaceAll(batch);
        await Task.Yield(); // let the single layout pass settle before the caller continues.

        // 与 SyncFoldToUi 尾部一致：同步 todo 全量快照 / deliverables / trajectory。
        Todos.Clear();
        foreach (var todo in _fold.Todos)
        {
            Todos.Add(todo);
        }
        Deliverables = _fold.Deliverables.ToArray();
        Trajectory = _fold.Trajectory;
        // The next streaming flush must re-publish even if the fold's version counters happen to
        // equal what we showed for the previous session.
        _publishedDeliverablesVersion = _fold.DeliverablesVersion;
        _publishedTrajectoryVersion = _fold.TrajectoryVersion;
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        try
        {
            ConnectionStatus = Loc.Get("Conn.Connecting");
            IsConnected = false;
            // P1-14: handshake via the shared scope's client, then attach (starts the shared
            // stream loop). Refresh is per-window so multiple windows share one connection.
            ConnectionScope.Instance.SetBaseUrl(HostUrl);
            // H1: persist the HostUrl we're actually about to handshake against, so a connect
            // (not just an edit) cements the address for next launch.
            PersistSettings();
            await _client.Call<object>(RpcMethods.HostDescribe, new { });
            ConnectionStatus = Loc.Get("Conn.Connected");
            IsConnected = true;
            ServiceRunning = true;
            ConnectionScope.Instance.MarkConnected();
            ConnectionScope.Instance.Attach();
            await RefreshWorkspacesAsync();
            await RefreshSessionsAsync();
        }
        catch (Exception ex)
        {
            ConnectionStatus = Loc.Format("Notify.ConnectFailed", ex.Message);
            IsConnected = false;
            ServiceRunning = false;
            NotifyErrorKey("Notify.ConnectFailed", ex.Message);
            await PrepareLaunchGuideAsync();
        }
    }

    /// <summary>
    /// After a failed connect, probe whether the backend is actually down and, if so, surface
    /// the launch guide: auto-locate a deepseek-harness checkout (or let the user pick one).
    /// </summary>
    private async Task PrepareLaunchGuideAsync()
    {
        // If the gateway answers after all (transient blip), skip the guide.
        if (await _launcher.IsRunningAsync()) return;

        LaunchStatus = Loc.Get("Svc.NotStarted");
        if (string.IsNullOrWhiteSpace(HarnessDirectory))
        {
            HarnessDirectory = HarnessLauncher.TryLocateDefaultDirectory() ?? "";
        }

        if (!string.IsNullOrWhiteSpace(HarnessDirectory))
        {
            LaunchStatus += Loc.Format("Svc.AutoLocated", HarnessDirectory);
        }
        else
        {
            LaunchStatus += Loc.Get("Svc.PickDirHint");
        }
    }

    /// <summary>Pick the deepseek-harness checkout directory (used to launch the backend).</summary>
    [RelayCommand]
    private async Task PickHarnessDirectoryAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.Get("Svc.PickDirTitle") };
        if (dialog.ShowDialog() == true)
        {
            HarnessDirectory = dialog.FolderName;
            PersistSettings();
            bool ok = HarnessLauncher.LooksLikeHarnessCheckout(HarnessDirectory);
            LogHarnessOp(ok
                ? Loc.Format("Svc.PickedDirLog", HarnessDirectory)
                : Loc.Format("Svc.PickedNonHarnessLog", HarnessDirectory));
            LaunchStatus = ok
                ? Loc.Format("Svc.PickedDir", HarnessDirectory)
                : Loc.Format("Svc.NotHarnessDir", HarnessDirectory);
            // Reflect the checkout's build state so the panel can offer "初始化并启动".
            HarnessNeedsBuild = HarnessLauncher.NeedsBuild(HarnessDirectory);
        }
        else
        {
            LogHarnessOp(Loc.Get("Svc.CancelPick"));
        }
    }

    /// <summary>Stop the backend process we started (called on window exit).</summary>
    public void StopHarness()
    {
        _launcher.Stop(_harnessProcess);
        _harnessProcess = null;
        HarnessStatusText = Loc.Get("Svc.Stopped");
        HarnessPid = 0;
        ServiceRunning = false;
        LogHarnessOp(Loc.Get("Svc.StopOnExit"));
        PersistSettings();
    }

    /// <summary>Append a process output line to the service log, trimming the oldest when over the cap.</summary>
    private void AppendHarnessLogLine(string line)
    {
        HarnessLog.Add(line);
        // Batch-trim from the front to avoid O(N^2) cost of repeated RemoveAt(0).
        if (HarnessLog.Count > MaxHarnessLogLines)
        {
            var excess = HarnessLog.Count - MaxHarnessLogLines;
            for (int i = 0; i < excess; i++)
                HarnessLog.RemoveAt(0);
        }
    }

    /// <summary>Append an operation log line (▸ prefix) for UI actions that have no process output.</summary>
    private void LogHarnessOp(string message)
    {
        AppendHarnessLogLine($"▸ {message}");
    }

    /// <summary>
    /// Marshal a process-output line from a background thread onto the UI thread before
    /// appending. <see cref="HarnessLauncher"/> raises output callbacks on its own worker
    /// threads (stdout/stderr readers), so callers must marshal before touching the collection.
    /// </summary>
    private void AppendHarnessLogLineMarshalled(string line)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && dispatcher.CheckAccess() is false)
            _ = dispatcher.InvokeAsync(() => AppendHarnessLogLine(line));
        else
            AppendHarnessLogLine(line);
    }

    /// <summary>Persist current preferences (harness directory, gateway URL, composer Enter action).</summary>
    private void PersistSettings()
    {
        _settings.HarnessDirectory = HarnessDirectory;
        _settings.HostUrl = HostUrl;
        _settings.BusyEnterAction = BusyEnterAction;
        _settings.Save();
    }

    partial void OnHostUrlChanged(string value) => PersistSettings();

    partial void OnBusyEnterActionChanged(string value) => PersistSettings();

    /// <summary>
    /// Surface a transient message in the bottom status bar (P0-1). The bar shows immediately
    /// and auto-clears after <see cref="NotificationDurationMs"/>. A newer notification replaces
    /// the pending one and resets its timer. Only the message-kind and text drive visibility;
    /// a notification never overwrites the persistent connection/service status texts.
    /// </summary>
    private void Notify(string message, string kind)
    {
        NotificationText = message;
        NotificationKind = kind;

        var cts = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _notificationCts, cts);
        prev?.Cancel();

        _ = AutoClearAsync(cts.Token);
    }

    private async Task AutoClearAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(NotificationDurationMs, ct);
            if (!ct.IsCancellationRequested)
            {
                NotificationText = "";
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Transient error notification (P0-1): red bar, RPC/approval/settings failures.</summary>
    private void NotifyError(string message) => Notify(message, "error");

    /// <summary>Localized error notification by resx key (ML).</summary>
    private void NotifyErrorKey(string key, params object[] args) =>
        Notify(Dsh.App.Services.Localization.Format(key, args), "error");

    /// <summary>Localized success notification by resx key (ML).</summary>
    private void NotifySuccessKey(string key, params object[] args) =>
        Notify(Dsh.App.Services.Localization.Format(key, args), "success");

    /// <summary>Transient info notification (P0-1): neutral blue bar, non-critical progress notes.</summary>
    private void NotifyInfo(string message) => Notify(message, "info");

    /// <summary>Transient success notification (P0-1): green bar, completed operations.</summary>
    private void NotifySuccess(string message) => Notify(message, "success");

    /// <summary>Collapse/expand the service-launch log status bar.</summary>
    [RelayCommand]
    private void ToggleHarnessLog() => ShowHarnessLog = !ShowHarnessLog;

    /// <summary>Collapse the whole service panel to a single status line.</summary>
    [RelayCommand]
    private void ToggleHarnessPanel() => ShowHarnessPanel = !ShowHarnessPanel;

    /// <summary>Stop the harness service from the UI (2): kills our launched tree and any port listener.</summary>
    [RelayCommand]
    private async Task StopServiceAsync()
    {
        if (IsLaunching) return;
        int pid = HarnessPid;
        LaunchStatus = Loc.Get("Svc.Stopping");
        LogHarnessOp($"停止服务（PID {pid}）…");
        try
        {
            await Task.Run(() => _launcher.Stop(_harnessProcess));
            _harnessProcess = null;
            ServiceRunning = false;
            IsConnected = false;
            HarnessPid = 0;
            HarnessStatusText = Loc.Get("Svc.Stopped");
            LaunchStatus = Loc.Get("Svc.StoppedDone");
            ConnectionStatus = Loc.Get("Conn.ServiceStopped");
            LogHarnessOp("服务已停止");
        }
        catch (Exception ex)
        {
            LaunchStatus = $"停止失败：{ex.Message}";
            LogHarnessOp($"停止失败：{ex.Message}");
        }
    }

    /// <summary>Refresh the harness backend status (1): gateway probe + listener PID.</summary>
    [RelayCommand]
    private async Task RefreshHarnessStatusAsync()
    {
        var status = await _launcher.DescribeStatusAsync();
        HarnessPid = status.ListenerPid;
        if (status.Running)
        {
            HarnessStatusText = status.ListenerPid > 0 ? Loc.Format("Svc.RunningPid", status.ListenerPid) : Loc.Get("Svc.Running");
            ServiceRunning = true;
            LogHarnessOp(status.ListenerPid > 0
                ? Loc.Format("Svc.RefreshRunningPid", status.ListenerPid)
                : Loc.Get("Svc.RefreshRunning"));
        }
        else
        {
            HarnessStatusText = Loc.Get("Svc.Stopped");
            HarnessPid = 0;
            ServiceRunning = false;
            LogHarnessOp(Loc.Get("Svc.RefreshStopped"));
        }
    }

    /// <summary>Abort an in-progress one-time init (pnpm install + build). Kills the pnpm tree.</summary>
    [RelayCommand]
    private void CancelInit()
    {
        if (!IsInitializing) return;
        try { _initCts?.Cancel(); } catch { /* already disposed */ }
        LogHarnessOp(Loc.Get("Svc.InitCancelledLog"));
    }

    /// <summary>Launch the backend service from the selected checkout and wait until it answers.</summary>
    [RelayCommand]
    private async Task StartHarnessAsync()
    {
        if (IsLaunching) return;

        if (string.IsNullOrWhiteSpace(HarnessDirectory) ||
            !HarnessLauncher.LooksLikeHarnessCheckout(HarnessDirectory))
        {
            LaunchStatus = Loc.Get("Svc.PickDirHint");
            return;
        }

        IsLaunching = true;
        try
        {
            LaunchStatus = Loc.Format("Svc.StartingDir", HarnessDirectory);
            HarnessLog.Clear();
            ShowHarnessLog = true;
            LogHarnessOp(Loc.Format("Svc.StartServiceLog", HarnessDirectory));

            // Phase 0 — one-time preparation. A fresh clone has no web client bundle
            // (apps/web-dist); `pnpm dsh web` would fail with MissingClientBundleError.
            // Run pnpm install + pnpm run build so the user can start straight from a raw
            // checkout. Re-check on each launch (cheap directory probe).
            HarnessNeedsBuild = HarnessLauncher.NeedsBuild(HarnessDirectory);
            if (HarnessNeedsBuild)
            {
                // Cancellable + time-boxed so a hung pnpm install never blocks the UI forever.
                _initCts?.Dispose();
                using var initCts = _initCts = new CancellationTokenSource(TimeSpan.FromMinutes(InitTimeoutMinutes));
                IsInitializing = true;
                try
                {
                    LaunchStatus = Loc.Get("Svc.Initializing");
                    LogHarnessOp(Loc.Get("Svc.InitializingLog"));
                    bool initialized = await _launcher.InitializeAsync(
                        HarnessDirectory,
                        line => AppendHarnessLogLineMarshalled(line),
                        initCts.Token);
                    if (initCts.IsCancellationRequested)
                    {
                        LaunchStatus = Loc.Get("Svc.InitCancelled");
                        HarnessNeedsBuild = true;
                        return;
                    }
                    if (!initialized)
                    {
                        HarnessNeedsBuild = true;
                        LaunchStatus = Loc.Get("Svc.InitFailed");
                        LogHarnessOp(Loc.Get("Svc.InitFailedLog"));
                        return;
                    }
                    HarnessNeedsBuild = false;
                }
                finally
                {
                    IsInitializing = false;
                    _initCts = null;
                }
            }

            _harnessProcess = _launcher.Launch(HarnessDirectory, line =>
            {
                // pnpm/process output arrives on a background thread; marshal to the UI
                // thread before touching the collection.
                AppendHarnessLogLineMarshalled(line);
            });
            if (_harnessProcess is null)
            {
                LaunchStatus = Loc.Get("Svc.StartPnpmFailed");
                LogHarnessOp(Loc.Get("Svc.StartPnpmFailedLog"));
                return;
            }

            LaunchStatus = Loc.Get("Svc.Starting");
            bool ready = await _launcher.WaitUntilReadyAsync(TimeSpan.FromMinutes(2));
            ServiceRunning = ready;
            if (ready)
            {
                HarnessPid = _launcher.FindListenerPid();
                HarnessStatusText = HarnessPid > 0 ? Loc.Format("Svc.RunningPid", HarnessPid) : Loc.Get("Svc.Running");
                LogHarnessOp(HarnessPid > 0
                    ? Loc.Format("Svc.ReadyPid", HarnessPid)
                    : Loc.Get("Svc.ReadyConnecting"));
                LaunchStatus = Loc.Get("Svc.Ready");
                ConnectionStatus = Loc.Get("Svc.ReadyConnecting");
                await ConnectAsync();
            }
            else
            {
                LaunchStatus = Loc.Get("Svc.StartTimeout");
                LogHarnessOp(Loc.Get("Svc.TimeoutNotReady"));
            }
        }
        catch (Exception ex)
        {
            LaunchStatus = Loc.Format("Svc.StartFailedMsg", ex.Message);
        }
        finally
        {
            IsLaunching = false;
        }
    }

    [RelayCommand]
    private async Task RenameSessionAsync(SessionItem? item)
    {
        if (item is null) return;

        // Lightweight rename prompt via a simple input dialog.
        var dialog = new RenameDialog(item.Title) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.NewTitle))
        {
            try
            {
                await _sessions.Rename(item.Id, dialog.NewTitle.Trim());
                item.Title = dialog.NewTitle.Trim();
            }
            catch (Exception ex)
            {
                _fold.Rows.Add(new SessionFold.Row("error", Loc.Format("Notify.RenameFailed", ex.Message)));
                NotifyErrorKey("Notify.RenameFailed", ex.Message);
                SyncFoldToUi();
            }
        }
    }

    [RelayCommand]
    private async Task RefreshSessionsAsync()
    {
        var list = await _sessions.List();
        Sessions.Clear();
        foreach (var summary in list.Items)
        {
            // title lives in the projections block (key=Title); absent → blank "新会话" / "(无标题)".
            string title = SummaryTitle(summary);
            Sessions.Add(new SessionItem
            {
                Id = summary.SessionId,
                Title = title,
                Cwd = summary.Cwd,
                Blank = summary.Blank,
                UpdatedAtText = FormatUpdatedAt(summary.UpdatedAt),
            });
        }
    }

    // B9: debounce typing in the search box so each keystroke doesn't fire session.search.
    // One shared timer is restarted on every change; only a 250ms pause triggers the RPC.
    private System.Windows.Threading.DispatcherTimer? _searchDebounce;

    /// <summary>
    /// CommunityToolkit generated partial method for <see cref="SearchQuery"/>. Debounces the
    /// search: 250ms after the user stops typing (or the box is cleared) we run the search;
    /// empty query just clears results without hitting the host.
    /// </summary>
    partial void OnSearchQueryChanged(string value)
    {
        if (_searchDebounce is null)
        {
            _searchDebounce = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250),
            };
            _searchDebounce.Tick += (_, _) =>
            {
                _searchDebounce.Stop();
                _ = SearchSessionsAsync();
            };
        }
        _searchDebounce.Stop();
        // Empty box → clear immediately (no debounce), so stale hits don't linger.
        if (string.IsNullOrWhiteSpace(value))
        {
            SearchHits.Clear();
            ConnectionStatus = "";
            return;
        }
        _searchDebounce.Start();
    }

    /// <summary>
    /// Search sessions by text snippet (B9, session.search). The host truncates the query
    /// and caps results; <c>HasMore</c> suggests narrowing. Results open the matching session.
    /// </summary>
    [RelayCommand]
    private async Task SearchSessionsAsync()
    {
        string query = SearchQuery.Trim();
        if (query.Length == 0)
        {
            SearchHits.Clear();
            ConnectionStatus = "";
            return;
        }
        IsSearching = true;
        try
        {
            var result = await _sessions.Search(query);
            SearchHits.Clear();
            foreach (var item in result.Items)
            {
                SearchHits.Add(new SessionSearchHit(item.SessionId, item.Snippet, query));
            }
            ConnectionStatus = result.HasMore
                ? Loc.Format("Svc.SearchFoundPlus", result.Items.Length)
                : Loc.Format("Svc.SearchFound", result.Items.Length);
        }
        catch (Exception ex)
        {
            ConnectionStatus = Loc.Format("Svc.SearchFailed", ex.Message);
            NotifyErrorKey("Notify.SearchFailed", ex.Message);
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>Open the session that a search hit refers to.</summary>
    [RelayCommand]
    private void OpenSearchHit(SessionSearchHit? hit)
    {
        if (hit is null) return;
        SelectedSessionId = hit.SessionId;
    }

    /// <summary>
    /// Load the slash-command catalog (C12) for the active session: merges skill.list names
    /// and agentPreset.list ids/names into <see cref="CommandCatalog"/>. Each becomes a
    /// <c>/&lt;name&gt;</c> candidate. Called on connect and on session switch.
    /// </summary>
    [RelayCommand]
    private async Task LoadCommandCatalogAsync()
    {
        if (SelectedSessionId is null) return;
        try
        {
            var skills = await _sessions.ListSkills(SelectedSessionId);
            var presets = await _sessions.ListAgentPresets();

            CommandCatalog.Clear();
            foreach (var s in skills.Skills)
            {
                CommandCatalog.Add(new CommandEntry(s.Name, s.Description, Loc.Get("Svc.SkillCategory")));
            }
            foreach (var p in presets.Presets)
            {
                string name = p.Name ?? p.Id;
                CommandCatalog.Add(new CommandEntry(name, p.IsDefault ? Loc.Get("Svc.PresetDefault") : Loc.Get("Svc.Preset"), Loc.Get("Svc.PresetCategory")));
            }
            // E2: client-local commands (handled by this client, not the host).
            CommandCatalog.Add(new CommandEntry("model", Loc.Get("Svc.SwitchModelCmd"), Loc.Get("Svc.Local")));
        }
        catch (Exception ex)
        {
            // P1-6: catalog is best-effort — a failure must not block the session. Keep the old
            // catalog (if any) so `/` still works, and surface a soft, non-blocking hint.
            if (CommandCatalog.Count == 0)
            {
                NotifyError($"命令目录加载失败，/ 命令暂不可用：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// Update the slash-command completion matches (C10) as the composer text changes.
    /// A leading <c>/</c> with text filters the catalog; empty slash shows all commands.
    /// </summary>
    [RelayCommand]
    private void UpdateCommandMatches()
    {
        string t = InputText ?? "";
        // P2-3: "@" opens a subagent-reference candidate list (mirroring the "/" command list).
        if (t.StartsWith('@'))
        {
            string frag = t[1..].TrimStart();
            CommandMatches.Clear();
            foreach (var sub in Subagents)
            {
                if (frag.Length == 0 || sub.Label.Contains(frag, StringComparison.OrdinalIgnoreCase))
                {
                    CommandMatches.Add(new CommandEntry(sub.Label, "子代理引用", "子代理"));
                }
            }
            SelectedCommandIndex = CommandMatches.Count > 0 ? 0 : -1;
            return;
        }
        if (!t.StartsWith('/'))
        {
            CommandMatches.Clear();
            SelectedCommandIndex = -1;
            return;
        }
        string frag2 = t[1..].TrimStart();
        CommandMatches.Clear();
        foreach (var c in CommandCatalog)
        {
            if (frag2.Length == 0 || c.Name.StartsWith(frag2, StringComparison.OrdinalIgnoreCase))
            {
                CommandMatches.Add(c);
            }
        }
        SelectedCommandIndex = CommandMatches.Count > 0 ? 0 : -1; // P1-7: default to first candidate.
    }
}
