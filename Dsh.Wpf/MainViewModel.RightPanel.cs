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
using BindingOperations = System.Windows.Data.BindingOperations;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace Dsh.Wpf;

/// <summary>
/// Hook for MainWindow to inject the composer-flush action so the view-model can
/// force the TextBox binding to write its current Text back to InputText before we
/// snapshot it for the optimistic user row. KeyBinding.Enter can fire SendCommand
/// before the binding's UpdateSourceTrigger has flushed.
/// </summary>
public static class ComposerBridge
{
    public static Action? FlushComposerToInputText { get; set; }
}

/// <summary>
/// Main-window view model: drives the MVP loop (connect → list sessions → prompt → stream
/// assistant text). Owns the client, the projection store, and the interaction coordinator,
/// and marshals downstream WebSocket frames onto the UI thread.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    [RelayCommand]
    private async Task LoadModelsAsync()
    {
        var log = Logging.Get<MainViewModel>();
        log.LogDebug("LoadModelsAsync entered (SelectedSessionId={SelectedSessionId})", SelectedSessionId);
        if (SelectedSessionId is null)
        {
            log.LogDebug("LoadModelsAsync skipped (no session selected)");
            return;
        }
        string sessionId = SelectedSessionId;
        ModelChoices.Clear();

        try
        {
            var result = await _sessions.GetModels(sessionId);
            // Stale-session guard: the user may have switched to another session (including a
            // same-named one) while this catalog load was in flight; never overwrite the active
            // session's model list with a stale session's result.
            if (SelectedSessionId != sessionId)
            {
                log.LogDebug("LoadModelsAsync discarded stale result for {SessionId} (now {Selected})", sessionId, SelectedSessionId);
                return;
            }
            // Defensive: the host schema guarantees non-null groups/models, but a runtime
            // adapter may yield a null/empty catalog list; never crash a session load on it.
            foreach (var group in result.Groups ?? [])
            {
                foreach (var model in group.Models ?? [])
                {
                    ModelChoices.Add(new ModelChoice(
                        Id: $"{group.Id}/{model.Id}",
                        Label: $"{group.Name} · {model.Name}",
                        Provider: group.Id,
                        Model: model.Id));
                }
            }

            // The session's current selection (from log/defaults) is always valid even when
            // the catalog is empty (the host treats catalog membership as advisory). Surface
            // it as at least one option so the user can see what is selected.
            if (result.Current is { } current)
            {
                var active = $"{current.Provider}/{current.Model}";
                if (!ModelChoices.Any(m => m.Id == active))
                {
                    ModelChoices.Insert(0, new ModelChoice(
                        Id: active,
                        Label: $"{current.Provider}/{current.Model}（当前）",
                        Provider: current.Provider,
                        Model: current.Model));
                }
                SelectedModelId = active;
                ModelLabel = current.Model;
            }

            // Surface catalog failures so an empty list is not silently confusing.
            if (ModelChoices.Count == 0 && result.Failures is { Length: > 0 })
            {
                ConnectionStatus = $"无可用模型（{result.Failures.Length} 个 provider catalog 失败）";
            }

            // E3: routable=false (no route registered) blocks the composer.
            IsRoutable = result.Routable;
            RoutableMessage = result.Routable
                ? ""
                : "当前会话没有可路由的模型（无 provider 路由）。请在设置中配置 provider 并恢复路由。";
        }
        catch (Exception ex)
        {
            _fold.Rows.Add(new SessionFold.Row("error", $"加载模型失败：{ex.Message}"));
            SyncFoldToUi();
        }
    }

    [RelayCommand]
    private async Task LoadSubagentsAsync()
    {
        if (SelectedSessionId is null) return;
        string parentSessionId = SelectedSessionId;
        Subagents.Clear();

        try
        {
            var catalog = await _sessions.ListSubagents(parentSessionId);
            // Stale-session guard: ignore subagents that arrived after the user switched away.
            if (SelectedSessionId != parentSessionId) return;
            foreach (var entry in catalog.Entries)
            {
                Subagents.Add(SubagentNodeUi.From(entry, parentSessionId));
            }
        }
        catch (Exception ex)
        {
            _fold.Rows.Add(new SessionFold.Row("error", $"加载子代理失败：{ex.Message}"));
            SyncFoldToUi();
        }
    }

    /// <summary>Load all settings namespaces into the settings panel (read-only MVP view).</summary>
    [RelayCommand]
    private async Task LoadSettingsAsync()
    {
        SettingsEntries.Clear();
        try
        {
            var result = await _sessions.DescribeSettings();
            foreach (var ns in result.Namespaces)
            {
                string valuePreview = ns.Value.HasValue
                    ? ns.Value.Value.GetRawText()
                    : "(无值)";
                SettingsEntries.Add(new SettingsEntry(ns.Ns, ns.Applies, ns.Revision, valuePreview));
            }
        }
        catch (Exception ex)
        {
            _fold.Rows.Add(new SessionFold.Row("error", $"加载设置失败：{ex.Message}"));
            SyncFoldToUi();
        }
    }

    /// <summary>Edit a settings path with CAS retry: on settings-conflict, re-read and retry.</summary>
    [RelayCommand]
    private async Task EditSettingsAsync(SettingsEntry? entry)
    {
        if (entry is null) return;

        var dialog = new InputDialog("编辑设置", $"编辑 {entry.Ns}（以 JSON 值）：");
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Text)) return;

        try
        {
            // CAS loop: read current revision, mutate with it; on conflict, re-read and retry.
            var describe = await _sessions.DescribeSettings();
            var nsView = describe.Namespaces.FirstOrDefault(n => n.Ns == entry.Ns);
            long revision = nsView?.Revision ?? 0;

            // Parse the input as JSON value to set at root.
            using var valueDoc = JsonDocument.Parse(dialog.Text.Trim());
            var op = SettingsOps.Set(new[] { "language" }, valueDoc.RootElement.Clone());

            while (true)
            {
                try
                {
                    var result = await _sessions.MutateSettings(entry.Ns, new[] { op }, revision);
                    _fold.Rows.Add(new SessionFold.Row("system", $"已更新设置 {entry.Ns}（rev {result.Revision}）"));
                    NotifySuccessKey("Notify.SettingsUpdated", entry.Ns, result.Revision);
                    break;
                }
                catch (RpcException rpc) when (rpc.Error.Code == RpcErrorCode.SettingsConflict)
                {
                    // P1-13: surface the CAS conflict (someone else changed the setting) instead of
                    // silently retrying; then re-read the latest revision and retry against it.
                    NotifyInfo($"设置 {entry.Ns} 已被他人修改，已重读最新版本并重试。");
                    var latest = await _sessions.DescribeSettings();
                    var latestNs = latest.Namespaces.FirstOrDefault(n => n.Ns == entry.Ns);
                    revision = latestNs?.Revision ?? revision;
                }
            }
            SyncFoldToUi();
            await LoadSettingsAsync();
        }
        catch (Exception ex)
        {
            _fold.Rows.Add(new SessionFold.Row("error", $"编辑设置失败：{ex.Message}"));
            NotifyErrorKey("Notify.EditSettingsFailed", ex.Message);
            SyncFoldToUi();
        }
    }

    /// <summary>Probe a provider endpoint for its model catalog.</summary>
    [RelayCommand]
    private async Task DiscoverModelsAsync()
    {
        DiscoveredModels.Clear();
        if (string.IsNullOrWhiteSpace(DiscoverProvider)) return;
        try
        {
            var result = await _sessions.DiscoverModels("llm", DiscoverProvider.Trim(), baseUrl: null, api: null);
            foreach (var model in result.Models)
            {
                DiscoveredModels.Add(model);
            }
        }
        catch (Exception ex)
        {
            _fold.Rows.Add(new SessionFold.Row("error", $"模型探测失败：{ex.Message}"));
            SyncFoldToUi();
        }
    }

    /// <summary>Load credential status for the known refs (values never cross this wire).</summary>
    [RelayCommand]
    private async Task LoadCredentialsAsync()
    {
        CredentialEntries.Clear();
        try
        {
            var refs = new List<string> { CredentialRef };
            var result = await _sessions.DescribeCredentials(refs);
            foreach (var (refName, view) in result.Credentials)
            {
                CredentialEntries.Add(CredentialEntry.From(refName, view));
            }
        }
        catch (Exception ex)
        {
            _fold.Rows.Add(new SessionFold.Row("error", $"加载凭据失败：{ex.Message}"));
            SyncFoldToUi();
        }
    }

    /// <summary>Save a credential from the password box (write-only; never read back).</summary>
    [RelayCommand]
    private async Task SaveCredentialAsync()
    {
        if (string.IsNullOrWhiteSpace(CredentialRef) || string.IsNullOrWhiteSpace(CredentialValue)) return;
        try
        {
            await _sessions.SetCredential(CredentialRef.Trim(), CredentialValue.Trim());
            CredentialValue = "";
            _fold.Rows.Add(new SessionFold.Row("system", $"已保存凭据：{CredentialRef.Trim()}"));
            SyncFoldToUi();
            await LoadCredentialsAsync();
        }
        catch (Exception ex)
        {
            _fold.Rows.Add(new SessionFold.Row("error", $"保存凭据失败：{ex.Message}"));
            SyncFoldToUi();
        }
    }

    /// <summary>Open a subagent's transcript (fold its events into the chat surface).</summary>
    [RelayCommand]
    private async Task OpenSubagentAsync(SubagentNodeUi? node)
    {
        if (node is null || node.IsDiagnostic) return;

        try
        {
            var history = await _sessions.GetSubagentHistory(node.ParentSessionId, node.Id, node.Mode);
            _fold.Rows.Add(new SessionFold.Row("system", $"—— 子代理 {node.Label} transcript ——"));

            foreach (var rawEvent in history.Events)
            {
                if (rawEvent is JsonElement el)
                {
                    _fold.Fold(el);
                }
            }
            SyncFoldToUi();
        }
        catch (Exception ex)
        {
            _fold.Rows.Add(new SessionFold.Row("error", $"加载子代理失败：{ex.Message}"));
            SyncFoldToUi();
        }
    }

    /// <summary>Continue a continuable subagent with a user message.</summary>
    [RelayCommand]
    private async Task ContinueSubagentAsync(SubagentNodeUi? node)
    {
        if (node is null || node.IsDiagnostic || node.Mode != "continuable") return;

        // Prompt for the continuation message.
        var dialog = new InputDialog("续写子代理", $"向 {node.Label} 发送：") { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Text)) return;

        try
        {
            var receipt = await _sessions.PromptSubagent(
                node.ParentSessionId,
                node.Id,
                node.Mode,
                new[] { PromptParts.Text(dialog.Text.Trim()) });
            _fold.Rows.Add(new SessionFold.Row("user", dialog.Text.Trim()));
            _fold.Rows.Add(new SessionFold.Row("system", $"已发送给子代理 {node.Label}"));
            SyncFoldToUi();
        }
        catch (Exception ex)
        {
            _fold.Rows.Add(new SessionFold.Row("error", $"续写失败：{ex.Message}"));
            SyncFoldToUi();
        }
    }

    /// <summary>Interrupt a running subagent.</summary>
    [RelayCommand]
    private async Task InterruptSubagentAsync(SubagentNodeUi? node)
    {
        if (node is null || node.IsDiagnostic) return;
        try
        {
            await _sessions.InterruptSubagent(node.ParentSessionId, node.Id, node.Mode);
            _fold.Rows.Add(new SessionFold.Row("system", $"已发送中断：{node.Label}"));
            SyncFoldToUi();
        }
        catch (Exception ex)
        {
            _fold.Rows.Add(new SessionFold.Row("error", $"中断失败：{ex.Message}"));
            SyncFoldToUi();
        }
    }

    [RelayCommand]
    private async Task SelectModelAsync()
    {
        if (SelectedModelId is null) return;
        var choice = ModelChoices.FirstOrDefault(m => m.Id == SelectedModelId);
        if (choice is null) return;

        // No active session: persist the choice in the status bar (ModelLabel) only — the
        // catalog refresh (LoadModelsAsync) will pick it up when a session becomes active.
        if (SelectedSessionId is null)
        {
            ModelLabel = choice.Model;
            return;
        }

        try
        {
            var result = await _sessions.SelectModel(SelectedSessionId, choice.Provider, choice.Model);
            ModelLabel = result.Selected.Model;
            _fold.Rows.Add(new SessionFold.Row("system", $"模型已切换为 {result.Selected.Provider}/{result.Selected.Model}"));
            NotifySuccessKey("Notify.ModelSwitched", result.Selected.Provider, result.Selected.Model);
            SyncFoldToUi();
        }
        catch (Exception ex)
        {
            _fold.Rows.Add(new SessionFold.Row("error", $"切换模型失败：{ex.Message}"));
            NotifyErrorKey("Notify.SwitchModelFailed", ex.Message);
            SyncFoldToUi();
        }
    }

    [RelayCommand]
    private async Task NewSessionAsync()
    {
        try
        {
            // B3: a workspace typically holds at most one blank placeholder session. If one
            // already exists for the current workspace, reuse it (open it) instead of creating
            // a fresh session every click — matching the Web client's `blank && cwd==path` rule.
            // Pull a fresh session list to bypass a stale Workspaces tree (the host may have
            // created a new blank we haven't refreshed into the tree yet).
            string? reusableId = await FindReusableBlankSessionAsync();
            if (reusableId is not null)
            {
                ConnectionStatus = $"已切换到会话 {reusableId}（复用空会话）";
                SelectedSessionId = reusableId;
                await RefreshWorkspacesAsync();
                await LoadHistoryAsync(reusableId);
                await LoadModelsAsync();
                await LoadSubagentsAsync();
                return;
            }

            ConnectionStatus = Loc.Get("Conn.CreatingSession");
            // Resolve a cwd for the new session: reuse the current session's cwd, or the
            // first known workspace's path. `session.create` requires workspaceId or cwd.
            string? cwd = ResolveNewSessionCwd();
            if (cwd is null)
            {
                ConnectionStatus = Loc.Get("Conn.CreateNoWorkspace");
                return;
            }

            // The host attaches the agent synchronously before responding; bound the
            // wait so a hung host surfaces as a clear message rather than freezing the UI.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var created = await _sessions.Create(cwd: cwd, ct: cts.Token);
            await RefreshWorkspacesAsync();
            await RefreshSessionsAsync();
            SelectedSessionId = created.SessionId;
            await LoadModelsAsync();
            await LoadSubagentsAsync();
            ConnectionStatus = $"已创建会话 {created.SessionId}";
        }
        catch (OperationCanceledException)
        {
            ConnectionStatus = Loc.Get("Conn.CreateTimeout");
            NotifyError(Loc.Get("Conn.CreateTimeout"));
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"创建会话失败：{ex.Message}";
            NotifyErrorKey("Notify.CreateSessionFailed", ex.Message);
        }
    }

    /// <summary>
    /// Find a reusable blank session for the current workspace (B3): a fresh <c>session.list</c>
    /// is consulted (not the potentially stale Workspaces tree) to find a <c>blank</c> session
    /// whose cwd matches the current workspace's path. Returns the session id, or null when
    /// none exists (in which case a fresh session should be created).
    /// </summary>
    private async Task<string?> FindReusableBlankSessionAsync()
    {
        string? wsPath = ResolveCurrentWorkspacePath();
        if (wsPath is null) return null;
        try
        {
            var list = await _sessions.List();
            foreach (var s in list.Items)
            {
                if (!s.Blank) continue;
                if (string.IsNullOrEmpty(s.Cwd)) continue;
                string sc = s.Cwd.Replace('/', '\\').TrimEnd('\\');
                string ws = wsPath.Replace('/', '\\').TrimEnd('\\');
                if (sc.Equals(ws, StringComparison.OrdinalIgnoreCase) ||
                    sc.StartsWith(ws + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    return s.SessionId;
                }
            }
        }
        catch (Exception)
        {
            // Best-effort: if session.list fails, fall through to create a new session.
        }
        return null;
    }

    /// <summary>The current workspace's path (selected session's cwd, else first workspace's path).</summary>
    private string? ResolveCurrentWorkspacePath()
    {
        if (SelectedSessionId is not null)
        {
            foreach (var ws in Workspaces)
            {
                foreach (var s in ws.Sessions)
                {
                    if (s.Id == SelectedSessionId && !string.IsNullOrWhiteSpace(s.Cwd))
                    {
                        return s.Cwd;
                    }
                }
            }
        }
        return Workspaces.FirstOrDefault()?.Path;
    }

    /// <summary>
    /// Pick a cwd for a brand-new session: the currently-selected session's cwd, then the
    /// first workspace's path, then the first known session's cwd. Null when nothing usable
    /// exists yet (the user must add a workspace first).
    /// </summary>
    private string? ResolveNewSessionCwd()
    {
        // 0. The most recently added workspace — lets the user create a session in a brand-new
        // workspace before its blank session/tree node becomes visible (建议3a).
        if (!string.IsNullOrWhiteSpace(_lastAddedWorkspacePath))
        {
            return _lastAddedWorkspacePath;
        }
        // 1. Selected session's cwd.
        if (SelectedSessionId is not null)
        {
            foreach (var ws in Workspaces)
            {
                foreach (var s in ws.Sessions)
                {
                    if (s.Id == SelectedSessionId && !string.IsNullOrWhiteSpace(s.Cwd))
                    {
                        return s.Cwd;
                    }
                }
            }
        }
        // 2. First workspace's path.
        var firstWorkspace = Workspaces.FirstOrDefault();
        if (firstWorkspace is not null && !string.IsNullOrWhiteSpace(firstWorkspace.Path))
        {
            return firstWorkspace.Path;
        }
        // 3. First known session's cwd.
        foreach (var ws in Workspaces)
        {
            foreach (var s in ws.Sessions)
            {
                if (!string.IsNullOrWhiteSpace(s.Cwd))
                {
                    return s.Cwd;
                }
            }
        }
        return null;
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        // Bug-A2: KeyBinding.Enter triggers SendCommand directly; the TextBox binding
        // may not have flushed the latest Text into InputText yet. Force a flush so
        // the optimistic user row we add in SendPromptAsync carries the real text.
        ComposerBridge.FlushComposerToInputText?.Invoke();

        // P1-8: the "alwaysSteer" preference makes plain Enter insert ahead of a running turn,
        // so the composer stays responsive even while the agent is busy.
        if (BusyEnterAction == "alwaysSteer" && ComposerMode == ComposerMode.Running)
        {
            await SendPromptAsync(mode: "steer");
            return;
        }
        await SendPromptAsync(mode: "queue");
    }

    /// <summary>
    /// Send the composer text as a steer (C5): a running session accepts a steer as an
    /// insertion ahead of the queued turns, instead of enqueueing behind them.
    /// </summary>
    [RelayCommand]
    private async Task SendSteerAsync()
    {
        ComposerBridge.FlushComposerToInputText?.Invoke();
        await SendPromptAsync(mode: "steer");
    }

    /// <summary>
    /// Shared prompt dispatch: text + pending images via <c>session.prompt</c>.
    /// The <paramref name="presetCommand"/> overload is retained for parity but no longer
    /// used by the chip paths — D3/D5 chips go through <c>settings.mutate</c> now.
    /// Returns true on success (host accepted the prompt and the result was ok or had no
    /// error code), false when guards failed / host rejected / send was refused.
    /// </summary>
    private async Task<bool> SendPromptAsync(string mode, string? presetCommand = null)
    {
        bool isPreset = !string.IsNullOrWhiteSpace(presetCommand);
        if (!isPreset && (string.IsNullOrWhiteSpace(InputText) || SelectedSessionId is null)) return false;
        if (SelectedSessionId is null) return false;
        if (IsSending) return false; // P0-4: prevent double-submit while a send is in flight.

        string sessionId = SelectedSessionId;
        string content = isPreset ? presetCommand! : InputText;
        if (!isPreset) InputText = ""; // P0-4: clear the composer immediately on send.

        // E2: client-local /model command — switches the model selection instead of sending
        // to the host. `/model` focuses the picker; `/model <name>` selects a match by id.
        // Preset commands (D3/D5 chips) skip this branch — they must reach the host.
        if (!isPreset && TryHandleLocalModelCommand(content))
        {
            return false;
        }

        // Preset commands are system toggles, not user speech — do not surface as a user row.
        if (!isPreset)
        {
            _fold.Rows.Add(new SessionFold.Row("user", content));
            SyncFoldToUi();
        }

        // P0-2: a steer briefly marks the composer as "插队中"; a queue send flips to running.
        // Preset commands (D3/D5) do NOT change ComposerMode — they are system toggles, not
        // an AI turn, so the composer must stay idle and the status bar must not show "running".
        if (!isPreset)
        {
            if (mode == "steer")
            {
                ComposerMode = ComposerMode.Steering;
            }
            else
            {
                ComposerMode = ComposerMode.Running;
            }
        }

        IsSending = true;
        try
        {
            var parts = new List<PromptPart> { PromptParts.Text(content) };
            if (_pendingImages.Count > 0)
            {
                parts.AddRange(_pendingImages);
                _pendingImages.Clear();
            }
            var result = await _sessions.Prompt(sessionId, parts, mode: mode);

            if (isPreset)
            {
                // Slash command result: host ran it through the command registry.
                // Success → state change reflected via plan/permissions projection; surface
                // the command's own text if any. Failure → command-error/unknown-command.
                if (!result.Accepted)
                {
                    NotifyError(Loc.Get("Conn.MessageRejected"));
                }
                else if (result.Command?.Kind == "error")
                {
                    NotifyError(result.Command.Text ?? Loc.Get("Cmd.Failed"));
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(result.Command?.Text))
                    {
                        NotifySuccess(result.Command.Text!);
                    }
                    return true; // preset command succeeded
                }
            }
            else
            {
                // P0-4: distinct send feedback — queue / steer / rejected.
                if (result.Accepted)
                {
                    if (mode == "steer")
                    {
                        ComposerMode = ComposerMode.Running; // steer settled; badge back to running.
                        NotifySuccess(Loc.Get("Conn.SteerInserted"));
                    }
                    else
                    {
                        NotifySuccess(Loc.Get("Conn.SentEnqueued"));
                    }
                }
                else
                {
                    NotifyError(Loc.Get("Conn.MessageRejected"));
                    ComposerMode = ComposerMode.Idle;
                }
            }
        }
        catch (RpcException rpc)
        {
            if (isPreset)
            {
                // Slash command admission/usage error (command-error / unknown-command).
                NotifyError($"[{rpc.Error.Code}] {rpc.Error.Message}");
            }
            else
            {
                _fold.Rows.Add(new SessionFold.Row("error", $"[{rpc.Error.Code}] {rpc.Error.Message}"));
                NotifyError($"[{rpc.Error.Code}] {rpc.Error.Message}");
                ComposerMode = ComposerMode.Idle; // P0-4: failed send must not leave a false "running" badge.
                SyncFoldToUi();
            }
        }
        catch (Exception ex)
        {
            if (isPreset)
            {
                NotifyError(ex.Message);
            }
            else
            {
                _fold.Rows.Add(new SessionFold.Row("error", ex.Message));
                NotifyError(ex.Message);
                ComposerMode = ComposerMode.Idle;
                SyncFoldToUi();
            }
        }
        finally
        {
            IsSending = false;
        }
        return false; // preset not successful (or non-preset path)
    }

    /// <summary>
    /// Handle the client-local <c>/model</c> command (E2). Returns true when the input was a
    /// local model command (consumed here, never sent to the host).
    /// <list type="bullet">
    /// <item><c>/model</c> — no arg: focus the model picker.</item>
    /// <item><c>/model &lt;query&gt;</c> — select the first catalog match by provider/model id.</item>
    /// </list>
    /// </summary>
    private bool TryHandleLocalModelCommand(string content)
    {
        if (!content.StartsWith('/')) return false;
        string[] parts = content.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].Equals("/model", StringComparison.OrdinalIgnoreCase)) return false;

        if (parts.Length < 2)
        {
            // No argument: signal the picker to open (focus handled by the view).
            ConnectionStatus = Loc.Get("Conn.PickModelHint");
            return true;
        }

        string query = parts[1].Trim();
        var match = ModelChoices.FirstOrDefault(m =>
            m.Model.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            m.Id.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            _fold.Rows.Add(new SessionFold.Row("error", $"未找到匹配的模型「{query}」"));
            SyncFoldToUi();
            return true;
        }

        _fold.Rows.Add(new SessionFold.Row("user", content));
        SyncFoldToUi();
        _ = ApplyModelChoiceAsync(match);
        return true;
    }

    /// <summary>Select a model by a catalog choice and surface the new selection (E2).</summary>
    private async Task ApplyModelChoiceAsync(ModelChoice choice)
    {
        if (SelectedSessionId is null) return;
        try
        {
            var result = await _sessions.SelectModel(SelectedSessionId, choice.Provider, choice.Model);
            SelectedModelId = choice.Id;
            ModelLabel = result.Selected.Model;
            _fold.Rows.Add(new SessionFold.Row("system", $"模型已切换为 {result.Selected.Provider}/{result.Selected.Model}"));
            NotifySuccessKey("Notify.ModelSwitched", result.Selected.Provider, result.Selected.Model);
            SyncFoldToUi();
        }
        catch (Exception ex)
        {
            _fold.Rows.Add(new SessionFold.Row("error", $"切换模型失败：{ex.Message}"));
            NotifyErrorKey("Notify.SwitchModelFailed", ex.Message);
            SyncFoldToUi();
        }
    }

    /// <summary>Count of images attached to the next prompt (for the composer label).</summary>
    [ObservableProperty]
    private int pendingImageCount;

    /// <summary>Attach an image from the clipboard (or a file via the picker) to the next prompt.</summary>
    [RelayCommand]
    private void AddImage()
    {
        try
        {
            if (Clipboard.ContainsImage())
            {
                var bitmap = Clipboard.GetImage();
                if (bitmap is null) return;
                using var ms = new MemoryStream();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(ms);
                _pendingImages.Add(new ImagePart
                {
                    MediaType = "image/png",
                    Data = Convert.ToBase64String(ms.ToArray()),
                    Name = "pasted.png",
                });
            }
            else
            {
                // Fall back to a file open dialog for explicit image selection.
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "图片 (*.png;*.jpg;*.jpeg;*.gif)|*.png;*.jpg;*.jpeg;*.gif",
                    Multiselect = true,
                };
                if (dlg.ShowDialog() == true)
                {
                    AddImageFiles(dlg.FileNames);
                }
            }
            PendingImageCount = _pendingImages.Count;
        }
        catch (Exception ex)
        {
            _fold.Rows.Add(new SessionFold.Row("error", $"添加图片失败：{ex.Message}"));
            NotifyErrorKey("Notify.AddImageFailed", ex.Message);
            SyncFoldToUi();
        }
    }

    /// <summary>
    /// Add one or more image files to the next prompt (P2-14), with batch validation: only
    /// supported image formats, and a per-file size cap. Invalid files are skipped with a notice.
    /// </summary>
    public void AddImageFiles(IEnumerable<string> paths)
    {
        int added = 0;
        var skipped = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                string media = ext switch
                {
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    _ => "",
                };
                if (media.Length == 0)
                {
                    skipped.Add($"{Path.GetFileName(path)}（不支持的格式）");
                    continue;
                }
                var fi = new FileInfo(path);
                if (fi.Length > 10 * 1024 * 1024) // 10MB cap.
                {
                    skipped.Add($"{Path.GetFileName(path)}（超过 10MB）");
                    continue;
                }
                byte[] bytes = File.ReadAllBytes(path);
                _pendingImages.Add(new ImagePart
                {
                    MediaType = media,
                    Data = Convert.ToBase64String(bytes),
                    Name = Path.GetFileName(path),
                });
                added++;
            }
            catch (Exception ex)
            {
                skipped.Add($"{Path.GetFileName(path)}（{ex.Message}）");
            }
        }
        PendingImageCount = _pendingImages.Count;
        if (added > 0) NotifyInfo(Dsh.App.Services.Localization.Format("Notify.ImagesAdded", added));
        if (skipped.Count > 0) NotifyError($"跳过 {skipped.Count} 个：{string.Join("；", skipped)}");
    }

    /// <summary>
    /// Capture an image already present in the clipboard (used by the Composer paste handler).
    /// </summary>
    public void AddPastedClipboardImage()
    {
        if (!Clipboard.ContainsImage()) return;
        var bitmap = Clipboard.GetImage();
        if (bitmap is null) return;
        using var ms = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(ms);
        _pendingImages.Add(new ImagePart
        {
            MediaType = "image/png",
            Data = Convert.ToBase64String(ms.ToArray()),
            Name = "pasted.png",
        });
        PendingImageCount = _pendingImages.Count;
        NotifyInfo(Dsh.App.Services.Localization.Get("Notify.ImageFromClipboard"));
    }

    /// <summary>Clear all pending images (P2-14).</summary>
    [RelayCommand]
    private void ClearImages()
    {
        _pendingImages.Clear();
        PendingImageCount = 0;
    }
}
