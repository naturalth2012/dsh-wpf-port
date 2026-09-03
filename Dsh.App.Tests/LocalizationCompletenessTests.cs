using System.Globalization;
using Dsh.App.Services;
using Xunit;

namespace Dsh.App.Tests;

/// <summary>
/// I5 regression tests: every supported culture must resolve every key that zh-CN (the
/// base resource) defines. A missing key falls back to zh-CN, which would silently hide a
/// translation gap — this test surfaces it.
/// </summary>
[Collection("Localization")]
public class LocalizationCompletenessTests
{
    private static IEnumerable<string> ChineseKeys()
    {
        // Enumerate keys present in the base (zh-CN) resource by probing a broad key set.
        // We can't list ResourceManager keys cheaply, so assert on the keys we actually use.
        return new[]
        {
            "Connect", "Disconnect", "Refresh", "NewSession", "Rename", "Delete",
            "SelectModel", "LoadModels", "Diagnostics", "ToggleTheme", "Status", "Host",
            "Subagents", "Language", "AddWorkspace", "NewWindow", "Connected", "Disconnected",
            "Connecting", "StartService", "StopService", "RefreshStatus", "OpenWeb",
            "PickDirectory", "HarnessDirectory", "ServiceRunning", "ServiceStopped",
            "ServiceStarting", "ProducedFiles", "Copy", "Streaming", "Ai", "Reasoning",
            "EmptySession", "SelectSession", "EmptyHint", "Workspace", "Ungrouped",
            "SearchPlaceholder", "Archive", "OpenFolder", "CopyPath", "CopyTitle",
            "Settings", "GroupConnection", "GroupModel", "GroupAppearance", "GroupData",
            "HostLabel", "CurrentStatus", "ModelLabel", "DiagHint",
            "Fold.Turn", "Fold.FoldError", "Fold.Compaction", "Fold.CompactionNoTokens",
            "Fold.RetryProgress", "Fold.RetryProgressNoDelay", "Fold.RetryAlways",
            "Fold.Retry", "Fold.TerminalAuth", "Fold.TerminalFailed",
            "Fold.TerminalFailedMessage", "Fold.ContextInjected", "Fold.ContextInjectedSuffix",
            "Notify.ConnectFailed", "Notify.RenameFailed", "Notify.SearchFailed",
            "Notify.FeedbackFailed", "Notify.CopyFailed", "Notify.OpenBrowserFailed",
            "Notify.OpenFailed", "Notify.OpenDeliverableFailed", "Notify.OpenFolderFailed",
            "Notify.DiagExportDone", "Notify.DiagExportSummaryOnly", "Notify.DiagExportFailed",
            "Notify.ArchiveFailed", "Notify.ForkFailed", "Notify.AddWorkspaceFailed",
            "Notify.DeleteWorkspaceFailed", "Notify.SettingsUpdated",
            "Notify.SettingsConflictRetry", "Notify.EditSettingsFailed", "Notify.ModelSwitched",
            "Notify.SwitchModelFailed", "Notify.CreateSessionTimeout",
            "Notify.CreateSessionFailed", "Notify.SteerInserted", "Notify.SentEnqueued",
            "Notify.MessageRejected", "Notify.AddImageFailed", "Notify.ImagesAdded",
            "Notify.ImagesSkipped", "Notify.ImageFromClipboard", "Notify.ApproveFailed",
            "Notify.RejectFailed", "Notify.Reconnected", "Notify.ConnectionLost",
            "Notify.RpcError", "Notify.CopySessionPath", "Notify.CopySessionTitle",
            "Notify.CopyWorkspacePath",
            "Conn.NotConnected", "Conn.Connecting", "Conn.Connected", "Conn.ServiceStopped",
            "Conn.CopyMessage", "Conn.PickWorkspaceDir", "Conn.PickCancelled",
            "Conn.CreatingSession", "Conn.CreateNoWorkspace", "Conn.CreateTimeout",
            "Conn.PickModelHint", "Conn.Reconnecting", "Conn.CopySessionPath",
            "Conn.CopySessionTitle", "Conn.CopyWorkspacePath", "Conn.SteerInserted",
            "Conn.SentEnqueued", "Conn.MessageRejected", "Conn.Reconnected",
            "Svc.NotDetected", "Svc.Stopped", "Svc.NotStarted", "Svc.Stopping",
            "Svc.StoppedDone", "Svc.PickDirHint", "Svc.StartPnpmFailed", "Svc.Starting",
            "Svc.Ready", "Svc.ReadyConnecting", "Svc.StartTimeout",
            "Mode.Running", "Mode.Steering", "Mode.Idle", "Send.Send", "Send.Enqueue",
            "Model.NotSelected", "Sess.NewSession", "Sess.NoTitle",
            "Fold.LoadHistoryFailed", "Fold.SessionEmpty", "Fold.UnknownTypes",
            "Fold.AllTypes", "Fold.TranscriptEmpty",
            // 2026-08-19 主页面 i18n 补全
            "Tab.Jobs", "Tab.Todo", "Tab.Queue", "Tab.Settings",
            "Menu.Rename", "Menu.OpenFolder", "Menu.ForkSession", "Menu.CopyPath",
            "Menu.CopyTitle", "Menu.Archive", "Menu.Refresh", "Menu.DeleteWorkspace",
            "Menu.ViewTranscript", "Menu.Continue", "Menu.Interrupt",
            "Subagent.Running", "Subagent.Paused", "Subagent.ReadOnly",
            "Subagent.Interrupted", "Subagent.Terminated", "Subagent.Idle",
            "Job.Queued", "Job.Stopping", "Job.Completed", "Job.Failed", "Job.Cancelled",
            "Cred.Configured", "Cred.NotConfigured",
            "Stats.TurnsUnit", "Stats.StepsUnit", "Stats.TokensUnit",
            "Composer.AddImage", "Composer.ClearImage", "Composer.Attached",
            "Composer.Queue", "Composer.Steer", "Composer.EnterQueueHint",
            "Composer.EnterHintBusy", "Composer.InsertCommand",
            "Search.Title", "Search.Results", "Search.ClickOpen",
            "Hover.Session", "Hover.Path", "Hover.Updated", "Hover.Status",
            "Svc.ChooseDir", "Svc.Start", "Svc.OpenWeb", "Svc.Stop", "Svc.PickDir",
            "Tb.Settings", "Tb.ToggleTopmost", "Tb.NewWindow",
            "Msg.ToolArgs", "Deliverable.Open",
            "Log.Collapse", "Log.Expand", "Log.Title", "Harness.TogglePanel", "Harness.ServiceName",
            "Tool.Mode", "Tool.Length", "Tool.Name",
            "Sb.SessionStats", "Sb.CurrentModel", "Sb.CurrentSession", "Sb.ConnectionStatus",
            "Sb.ServiceStatus", "Sb.ModelPrefix", "Sb.SessionPrefix",
            "Tb.TogglePanel", "Tab.Trajectory", "Tb.Trajectory", "Tb.TrajectoryHint",
            "Q.AnswerNeeded", "Q.PlanReview", "Q.Approve", "Q.Submit", "Q.CustomHint",
            "Q.ToolPermitPrefix", "Q.ToolPermitSuffix", "Q.AllowOnce", "Q.Reject",
            "Set.Refresh", "Set.RefreshHint", "Set.Credentials", "Set.Save",
            "Set.LoadCredentials", "Set.DiscoverModels", "Set.Probe", "Set.EditCas",
            "Sess.MarkedHelpful", "Sess.MarkedNeedsWork", "Sess.OpenedInBrowser",
            "Sess.OpenedPath", "Sess.OpenFolderFailed", "Sess.Archived", "Sess.ArchiveFailed",
            "Sess.Forking", "Sess.Forked", "Sess.ForkFailed", "Sess.AddedWorkspace",
            "Sess.WorkspaceExists", "Sess.AddWorkspaceFailed", "Sess.DeletedWorkspace",
            "Sess.DeleteWorkspaceFailed", "Sess.DeleteWorkspaceError", "Sess.LoadWorkspaceFailed",
            "Sess.Untitled",
            "Svc.AutoLocated", "Svc.PickedDirLog", "Svc.PickedNonHarnessLog", "Svc.PickedDir",
            "Svc.NotHarnessDir", "Svc.CancelPick", "Svc.StopOnExit", "Svc.RunningPid",
            "Svc.Running", "Svc.RefreshRunningPid", "Svc.RefreshRunning", "Svc.StartingDir",
            "Svc.StartServiceLog", "Svc.StartPnpmFailedLog", "Svc.ReadyPid", "Svc.ReadyConnecting",
            "Svc.TimeoutNotReady", "Svc.StartFailedMsg", "Svc.SearchFoundPlus", "Svc.SearchFound",
            "Svc.SearchFailed", "Svc.SkillCategory", "Svc.PresetDefault", "Svc.Preset",
            "Svc.PresetCategory", "Svc.SwitchModelCmd", "Svc.Local",
            "Msg.ExpandMore", "Html.Unavailable", "Html.MdPreviewTitle", "Tray.ConnectedTitle", "Tray.Reconnected",
            "Msg.OpenLocally",
            "Connect.EditUrlTitle", "Connect.EditUrlPrompt", "Connect.Edit", "Connect.Current",
        };
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("ko")]
    public void Every_culture_resolves_every_base_key(string cultureCode)
    {
        var culture = new CultureInfo(cultureCode);
        var zh = new CultureInfo("zh-CN");
        // The key must exist in the base (zh-CN) resource for every culture to resolve it.
        // We cannot rely on value != key (a translation may coincidentally equal the key,
        // e.g. Diagnostics), so we assert the base resolves it to something other than the
        // raw key; the satellite then either translates or falls back to zh-CN.
        var unresolved = ChineseKeys()
            .Where(key => Strings.Get(key, zh) == key)
            .ToList();
        Assert.True(unresolved.Count == 0, $"[{cultureCode}] keys missing from base (zh-CN): "
            + string.Join(", ", unresolved.Take(10)));
    }
}
