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
    private readonly ProjectionStore _projections = new();
    private readonly InteractionCoordinator _interactions;
    private readonly SessionFold _fold = new();
    private readonly List<ImagePart> _pendingImages = new();
    // === 历史分页状态（P2: tail 页 + loadOlder 向上翻页）===
    private long? _historyCursor;      // 当前已加载的最早事件 seq（loadOlder 游标）
    private bool _historyHasMore;      // 前面是否还有更早历史
    private bool _historyLoadingOlder; // loadOlder 进行中防重入

    /// <summary>Raised after a loadOlder page has been inserted at the head of
    /// <see cref="Transcript"/>; the argument is the item that was previously first, which the
    /// view should <c>ScrollIntoView</c> to preserve the user's scroll position.</summary>
    public event Action<object>? ScrollToAnchorRequested;
    private readonly HarnessLauncher _launcher = new();
    /// <summary>Newly added workspace path, used to target the next New Session into the fresh
    /// workspace before its tree node/blank session is visible (建议3a).</summary>
    private string? _lastAddedWorkspacePath;
    /// <summary>流式节流：把每个 chunk 的 Transcript 同步合并为低频 UI 更新（自适应防抖）。</summary>
    private UiThrottle? _uiThrottle;
    /// <summary>ConnectionScope 的事件订阅令牌（解决旧版 += 后无 -= 导致的窗口关闭后 VM 仍被单例持有的泄漏）。</summary>
    private readonly List<IDisposable> _subscriptions = new();
    private bool _disposed;

    /// <summary>Shared HTTP/WS client (P1-14) owned by <see cref="ConnectionScope"/>.</summary>
    private WpfApiClient _client => ConnectionScope.Instance.Client;

    /// <summary>Shared session service (P1-14) owned by <see cref="ConnectionScope"/>.</summary>
    private SessionService _sessions => ConnectionScope.Instance.Sessions;

    /// <summary>
    /// Whether this window owns the shared workspace tree (P1-14 step 3). The first window is
    /// "main" and handles workspace-level host frames; secondary windows focus a single session
    /// and ignore workspace mutations to avoid racing the shared tree.
    /// </summary>
    public bool IsMainWindow { get; set; } = true;
    private readonly AppSettings _settings = AppSettings.Load();

    [ObservableProperty]
    private string hostUrl = "http://127.0.0.1:3080";

    /// <summary>
    /// Enter-action preference (P1-8): "queue" = Enter sends a queued message; "alwaysSteer" =
    /// Enter during a busy session sends as a steer. Persisted via <see cref="AppSettings"/>.
    /// </summary>
    [ObservableProperty]
    private string busyEnterAction = "queue";

    [ObservableProperty]
    private string connectionStatus = Loc.Get("Conn.NotConnected");

    /// <summary>True while the gateway is connected (drives the status-bar color).</summary>
    [ObservableProperty]
    private bool isConnected;

    /// <summary>Path to the deepseek-harness checkout (used to launch the backend).</summary>
    [ObservableProperty]
    private string harnessDirectory = "";

    /// <summary>True once the host gateway is confirmed up (drives the launch guide's visibility).</summary>
    [ObservableProperty]
    private bool serviceRunning;

    /// <summary>True while a launch attempt is in progress (disables the launch button).</summary>
    [ObservableProperty]
    private bool isLaunching;

    /// <summary>
    /// True when the selected harness checkout exists but has never been built (<c>apps/web-dist</c>
    /// missing). Drives the "未构建" state and the 「初始化并启动」button. Refreshed on directory
    /// pick / status refresh so the panel always reflects the checkout's real state.
    /// </summary>
    [ObservableProperty]
    private bool harnessNeedsBuild;

    /// <summary>
    /// True while the one-time install + build is running. Drives the 「取消」button and prevents
    /// re-entrant start. Separate from <see cref="IsLaunching"/> so the UI can show a dedicated
    /// cancel affordance during the (potentially minutes-long) init phase.
    /// </summary>
    [ObservableProperty]
    private bool isInitializing;

    /// <summary>Progress / log text of the launch attempt.</summary>
    [ObservableProperty]
    private string launchStatus = "";

    /// <summary>Raw stdout/stderr lines of the launch process (service status bar).</summary>
    public ObservableCollection<string> HarnessLog { get; } = new();

    /// <summary>Whether the service log status bar is expanded (collapsible section).</summary>
    [ObservableProperty]
    private bool showHarnessLog;

    /// <summary>Whether the service management panel is expanded (collapsible to one line).</summary>
    [ObservableProperty]
    private bool showHarnessPanel = true;

    /// <summary>Guard against unbounded growth of <see cref="HarnessLog"/> (last N lines kept).</summary>
    private const int MaxHarnessLogLines = 500;
    // Cap the number of transcript rows rendered at once to keep long sessions responsive
    // (P2-15). The underlying fold still holds the full history; we only trim what reaches the UI.
    // 600 (down from 2000) bounds both initial realize cost and streaming re-sync cost; combined
    // with ListBox UI virtualization only the visible window's ChatEntry controls are ever built.
    private const int MaxTranscriptRenderLines = 600;

    /// <summary>Sentinel workspace id for the transient "未分组" bucket for ungrouped sessions.</summary>
    private const string OrphanWorkspaceId = "__orphans__";

    /// <summary>Backend process handle so the window can stop it on exit.</summary>
    private Process? _harnessProcess;

    /// <summary>
    /// Cancellation source for the one-time init phase (pnpm install + build). Lets the user
    /// abort a long-running first-time setup via the 「取消」button; the launcher kills the
    /// pnpm process tree when this fires.
    /// </summary>
    private CancellationTokenSource? _initCts;

    /// <summary>Hard timeout for the init phase (install + build). A hung pnpm must not block the UI forever.</summary>
    private const int InitTimeoutMinutes = 15;

    /// <summary>Human-readable status of the harness backend (1): running PID / stopped / unknown.</summary>
    [ObservableProperty]
    private string harnessStatusText = Loc.Get("Svc.NotDetected");

    /// <summary>PID of the process listening on the gateway port (0 = none), for display.</summary>
    [ObservableProperty]
    private int harnessPid;

    [ObservableProperty]
    private string inputText = "";

    partial void OnInputTextChanged(string value) => UpdateCommandMatches();

    // ---- D3 / D5 chip commands ----
    // ALIGNED WITH WEB (packages/client/ui-permission-presets/src/client/settings-store.ts):
    // chips dispatch via settings.mutate — NOT via session.prompt. The shipped web UI has no
    // `/permission` chip firing through session.prompt; it writes
    //   settings.mutate({ ns: 'permission', ops:[{op:'set',path:['defaultPreset'],value}], expectedRevision })
    // and reconciles from the mirror. session.prompt (slash command) would route through the
    // host command registry, which in this harness build is not always wired for these presets
    // (the test scaffold uses settings.mutate instead). settings.mutate is a non-prompt write:
    // it never goes to the model, never starts an AI turn, never adds a user row.
    //
    // The chip is a session-level mode toggle: optimistically update the local view first so
    // the UI reflects the click immediately, then persist via settings.mutate with CAS retry.
    // On settings-conflict, re-read the latest revision and retry (matches EditSettingsAsync).
    /// <summary>Toggle the plan chip. Currently this UI only exposes the OFF direction; the
    /// toolbar is the entry into plan mode.</summary>
    [RelayCommand]
    private async Task TogglePlan()
    {
        if (!IsPlanMode) return; // OFF chip only — entering plan mode is toolbar-driven.
        await ApplyPlanSettingsAsync("off");
    }

    /// <summary>Switch the active permission preset by writing
    /// <c>permission/defaultPreset</c> via <c>settings.mutate</c> with CAS retry.</summary>
    [RelayCommand]
    private async Task SetPermission(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        // Optimistic local update so the chip reflects the click immediately.
        PermissionCurrent = value;
        await MutatePermissionDefaultPresetAsync(value);
    }

    /// <summary>Set the plan setting via settings.mutate. Mirrors SetPermission for the plan chip.</summary>
    private async Task ApplyPlanSettingsAsync(string mode)
    {
        try
        {
            await MutateSettingWithCasAsync(
                "plan",
                new[] { SettingsOps.Set(new[] { "mode" }, JsonSerializer.SerializeToElement(mode)) });
            IsPlanMode = mode == "on";
        }
        catch (Exception ex)
        {
            Logging.Get<MainViewModel>().LogWarning("ApplyPlanSettingsAsync failed: {Err}", ex.Message);
            NotifyErrorKey("Notify.EditSettingsFailed", ex.Message);
        }
    }

    /// <summary>Persist a new permission default preset via settings.mutate (CAS retry on conflict).</summary>
    private async Task MutatePermissionDefaultPresetAsync(string preset)
    {
        try
        {
            await MutateSettingWithCasAsync("permission", new[] { SettingsOps.Set(new[] { "defaultPreset" }, JsonSerializer.SerializeToElement(preset)) });
        }
        catch (Exception ex)
        {
            Logging.Get<MainViewModel>().LogWarning("MutatePermissionDefaultPresetAsync failed: {Err}", ex.Message);
            NotifyErrorKey("Notify.EditSettingsFailed", ex.Message);
        }
    }

    /// <summary>CAS-loop helper: read current revision, mutate with it, retry on settings-conflict
    /// by re-reading the latest revision. Mirrors <c>EditSettingsAsync</c>.</summary>
    private async Task MutateSettingWithCasAsync(
        string ns,
        IReadOnlyList<SettingsPathOp> ops,
        Action<long>? into = null)
    {
        var describe = await _sessions.DescribeSettings();
        long revision = describe.Namespaces.FirstOrDefault(n => n.Ns == ns)?.Revision ?? 0;
        while (true)
        {
            try
            {
                var result = await _sessions.MutateSettings(ns, ops, revision);
                into?.Invoke(result.Revision);
                return;
            }
            catch (RpcException rpc) when (rpc.Error.Code == RpcErrorCode.SettingsConflict)
            {
                NotifyInfo($"设置 {ns} 已被他人修改，已重读最新版本并重试。");
                var latest = await _sessions.DescribeSettings();
                revision = latest.Namespaces.FirstOrDefault(n => n.Ns == ns)?.Revision ?? revision;
            }
        }
    }

    /// <summary>UI hook: ComposerBox needs focus after a chip fills the input. Set by the window owner.</summary>
    public Action? FocusComposer;

    /// <summary>Active UI theme name, "Light" or "Dark" (P2-16). Persisted on change.</summary>
    [ObservableProperty]
    private string theme = ThemeSettings.Load().Theme;

    /// <summary>Toggle between Light and Dark themes and persist the choice (P2-16).</summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        Theme = Theme switch
        {
            "Dark" => "Light",
            _ => "Dark",
        };
        App.ApplyTheme(Theme);
        new ThemeSettings { Theme = Theme }.Save();
    }

    /// <summary>Active UI language code, e.g. "zh-CN" / "en" / "fr" (ML). Persisted on change.</summary>
    [ObservableProperty]
    private string language = AppSettings.Load().Language;

    /// <summary>All selectable languages (code + native name), for the language dropdown (ML).</summary>
    public IReadOnlyList<Dsh.App.Services.Localization.Language> SupportedLanguages
        => Dsh.App.Services.Localization.Supported;

    /// <summary>Apply + persist the selected language (ML). Fired on dropdown change; field init on
    /// construction skips this (App startup already applies the persisted code).</summary>
    partial void OnLanguageChanged(string value)
    {
        Dsh.App.Services.Localization.SetLanguage(value);
        new AppSettings { Language = value }.Save();
        // 刷新所有依赖本地化文本的 VM 计算属性（如 KindText、状态栏文案）：
        // XAML 的 {loc:Loc} 由 Localization.Signal 处理，VM 侧属性需主动通知。
        OnPropertyChanged(string.Empty);
        // K4: re-apply the language-driven UI font fallback chain to the window roots.
        ApplyUiFont();
    }

    /// <summary>K4: point the main window root FontFamily at the language-driven fallback chain
    /// (CJK vs Latin lead). Public so MainWindow can also call it on Loaded (the startup path,
    /// where the field-init of Language skips OnLanguageChanged and the window isn't built yet).</summary>
    public void ApplyUiFont()
    {
        string font = Dsh.App.Services.Localization.UiFont;
        if (System.Windows.Application.Current?.MainWindow is { } main)
        {
            main.FontFamily = new System.Windows.Media.FontFamily(font);
        }
    }

    /// <summary>"Always on top" window state (K4). Restored from persisted settings on construction.</summary>
    [ObservableProperty]
    private bool isTopmost = AppSettings.Load().Topmost;

    /// <summary>Toggle the "always on top" window state and persist the choice (K4).</summary>
    [RelayCommand]
    private void ToggleTopmost()
    {
        IsTopmost = !IsTopmost;
        new AppSettings { Topmost = IsTopmost }.Save();
    }

    [ObservableProperty]
    private string? selectedSessionId;

    /// <summary>Title of the currently selected session, surfaced prominently at the top of the
    /// content area (not just the sidebar highlight).</summary>
    [ObservableProperty]
    private string selectedSessionTitle = "";

    /// <summary>The active approval awaiting a user decision, if any.</summary>
    [ObservableProperty]
    private ApprovalRequest? pendingApproval;

    /// <summary>Current question batch rpcId awaiting answers, if any.</summary>
    [ObservableProperty]
    private RpcId? pendingQuestion;

    /// <summary>Session id of the current pending question batch.</summary>
    [ObservableProperty]
    private string? pendingQuestionSession;

    /// <summary>Bindable question list for the current batch (empty when none pending).</summary>
    public ObservableCollection<QuestionUi> PendingQuestions { get; } = new();

    public ObservableCollection<SessionItem> Sessions { get; } = new();
    public ObservableCollection<WorkspaceNode> Workspaces { get; } = new();
    public ObservableCollection<SessionSearchHit> SearchHits { get; } = new();
    public ObservableCollection<CommandEntry> CommandCatalog { get; } = new();
    public ObservableCollection<CommandEntry> CommandMatches { get; } = new();

    /// <summary>Selected index in the <c>/</c> command candidate list (P1-7, keyboard nav).</summary>
    [ObservableProperty]
    private int selectedCommandIndex = -1;

    /// <summary>Search box text (B9, session.search). Empty = no search results shown.</summary>
    [ObservableProperty]
    private string searchQuery = "";

    /// <summary>True while a search is in flight (drives the progress indicator).</summary>
    [ObservableProperty]
    private bool isSearching;
    /// <summary>True while a history load is rendering in batches; suppresses AutoScrollBehavior's
    /// per-batch scroll so the view doesn't jump repeatedly, and re-pins to the bottom on resume.</summary>
    [ObservableProperty]
    private bool isLoadingHistory;
    // Single stable collection instance (NOT reassigned). Re-syncs go through
    // ObservableCollectionEx.ReplaceAll, which raises exactly ONE Reset notification instead of one
    // CollectionChanged per row — for a 600-row window that's 600x fewer layout passes, which is
    // what keeps a large session from freezing. Keeping the same instance also avoids raising the
    // VM's PropertyChanged off the UI thread (a background history fold would otherwise deadlock
    // WPF bindings and freeze the window on connect).
    public ObservableCollectionEx<ChatEntry> Transcript { get; } = new();

    /// <summary>Files produced by mutation tools in the current turn (P2-5, H3).</summary>
    [ObservableProperty]
    private IReadOnlyList<SessionFold.DeliverableItem> deliverables = [];

    /// <summary>Version of the fold deliverables last published to <see cref="Deliverables"/>.
    /// Same pattern as the trajectory: the fold hands out one live List instance, so a plain
    /// assignment cannot detect mutations by reference and the produced-files bar never updated.</summary>
    private int _publishedDeliverablesVersion = -1;

    /// <summary>Step-by-step trajectory of surface events for the loaded session (P2-6, H1).</summary>
    [ObservableProperty]
    private IReadOnlyList<SessionFold.TrajectoryStep> trajectory = [];

    /// <summary>Version of the fold trajectory last published to <see cref="Trajectory"/>.
    /// -1 forces the first publish. A version (not a reference) is required because the fold
    /// hands out the same live List instance every time — see SyncFoldToUi.</summary>
    private int _publishedTrajectoryVersion = -1;

    /// <summary>Right-hand helper panel width (U3); restored from persisted settings (阶段3) so a
    /// dragged width survives restarts. 0 means collapsed.</summary>
    [ObservableProperty]
    private System.Windows.GridLength rightPanelWidth = new(LoadPersistedPanelWidth());

    /// <summary>True once the user manually toggled the right panel, disabling auto responsive collapse (D5).</summary>
    private bool rightPanelUserPinned;

    /// <summary>True when the right-hand panel is expanded (width &gt; 0).
    /// Drives the toggle glyph / visibility. The button that binds this MUST live OUTSIDE the
    /// collapsing panel: previously it was placed inside, so clicking it hid the panel and the
    /// button along with it, leaving no way to bring the panel back.</summary>
    public bool RightPanelVisible => RightPanelWidth.Value > 0;

    partial void OnRightPanelWidthChanged(System.Windows.GridLength value)
    {
        OnPropertyChanged(nameof(RightPanelVisible));
        OnPropertyChanged(nameof(IsRightPanelWide));
    }

    /// <summary>Default and wide widths for the right-hand panel (U3).
    /// The trajectory ledger (turn/request grouped table + details pane) is unreadable at the
    /// default width, so the user can widen the panel instead of the ledger being cramped.</summary>
    private const double RightPanelDefaultWidth = 240;
    private const double RightPanelWideWidth = 560;

    /// <summary>Bounds applied to a dragged width so the panel can never become unusable or
    /// swallow the whole window (阶段3).</summary>
    public const double RightPanelMinWidth = 180;
    public const double RightPanelMaxWidth = 900;

    /// <summary>Last non-zero panel width; restored when re-expanding after a collapse.
    /// Initialised from persisted settings so a dragged width survives restarts.</summary>
    private double _lastRightPanelWidth = LoadPersistedPanelWidth();

    /// <summary>Read the persisted panel width, clamped to the legal range so a hand-edited or
    /// stale settings file can't produce an unusable layout.</summary>
    private static double LoadPersistedPanelWidth()
    {
        double saved = Dsh.App.Services.AppSettings.Load().RightPanelWidth;
        if (saved <= 0) return RightPanelDefaultWidth;
        if (saved < RightPanelMinWidth) return RightPanelMinWidth;
        if (saved > RightPanelMaxWidth) return RightPanelMaxWidth;
        return saved;
    }

    /// <summary>Clamp a width into the legal range.</summary>
    public static double ClampPanelWidth(double width)
    {
        if (width <= 0) return 0;
        if (width < RightPanelMinWidth) return RightPanelMinWidth;
        if (width > RightPanelMaxWidth) return RightPanelMaxWidth;
        return width;
    }

    /// <summary>
    /// Set the panel width from a splitter drag, clamping it and persisting the result.
    /// Called once on DragCompleted — NOT continuously — because every write here touches
    /// AppSettings (a JSON serialize + file write), and doing that per mouse-move would mean
    /// hundreds of synchronous disk writes per drag.
    /// </summary>
    public void SetRightPanelWidthFromDrag(double width)
    {
        double clamped = ClampPanelWidth(width);
        if (clamped > 0) _lastRightPanelWidth = clamped;
        RightPanelWidth = new System.Windows.GridLength(clamped);
        PersistPanelWidth();
    }

    /// <summary>Persist the current panel width (best-effort; AppSettings.Save never throws).</summary>
    private void PersistPanelWidth()
    {
        try
        {
            var settings = Dsh.App.Services.AppSettings.Load();
            settings.RightPanelWidth = RightPanelWidth.Value;
            settings.Save();
        }
        catch
        {
            // Best-effort: a failed persist must not break the drag interaction.
        }
    }

    /// <summary>True while the panel is at the wide width (drives the toggle glyph).</summary>
    public bool IsRightPanelWide => RightPanelWidth.Value >= RightPanelWideWidth - 1.0;

    /// <summary>Toggle the right-hand helper panel (作业/队列/轨迹) open or collapsed (U3).</summary>
    [RelayCommand]
    private void ToggleRightPanel()
    {
        rightPanelUserPinned = true;
        if (RightPanelWidth.Value > 0)
        {
            _lastRightPanelWidth = RightPanelWidth.Value;   // remember for re-expand
            RightPanelWidth = new System.Windows.GridLength(0);
        }
        else
        {
            RightPanelWidth = new System.Windows.GridLength(_lastRightPanelWidth);
        }
        PersistPanelWidth();
    }

    /// <summary>Switch the right panel between the default and the wide width so the trajectory
    /// ledger has room. Expanding from a collapsed state jumps straight to the wide width.</summary>
    [RelayCommand]
    private void TogglePanelWidth()
    {
        rightPanelUserPinned = true;
        double current = RightPanelWidth.Value;
        if (current <= 0)
        {
            _lastRightPanelWidth = RightPanelWideWidth;
            RightPanelWidth = new System.Windows.GridLength(RightPanelWideWidth);
            PersistPanelWidth();
            return;
        }
        // A dragged width counts as "wide", so the button collapses it back to the default
        // rather than getting stuck toggling between two values the user never chose.
        double next = current <= RightPanelDefaultWidth + 1.0
            ? RightPanelWideWidth
            : RightPanelDefaultWidth;
        _lastRightPanelWidth = next;
        RightPanelWidth = new System.Windows.GridLength(next);
        PersistPanelWidth();
    }

    /// <summary>
    /// Copy the entire <see cref="HarnessLog"/> to the clipboard (one line per row) — bound to
    /// the "复制全部" button in the service log header and to the ListBox's default Ctrl+C
    /// key binding. <see cref="CopyHarnessLogSelection"/> is responsible for the "selection vs
    /// all" branch; this method is the unconditional "all" path used by the button.
    /// </summary>
    [RelayCommand]
    private void CopyHarnessLogAll()
    {
        var text = string.Join(Environment.NewLine, HarnessLog);
        TrySetClipboardText(text);
    }

    /// <summary>
    /// Copy the currently-selected rows of <see cref="HarnessLog"/> to the clipboard, or the
    /// full log when no selection exists. The View passes the selected-string snapshot because
    /// CommunityToolkit's <c>RelayCommand</c> marshals command parameters synchronously, which
    /// keeps this method off the dispatcher hot path.
    /// </summary>
    /// <param name="selectedLines">Pre-marshalled snapshot of selected rows (may be null/empty).</param>
    [RelayCommand]
    private void CopyHarnessLogSelection(System.Collections.IList? selectedLines)
    {
        IEnumerable<string> lines = HarnessLog;
        if (selectedLines is { Count: > 0 })
        {
            var sel = new List<string>(selectedLines.Count);
            foreach (var o in selectedLines)
                if (o is string s) sel.Add(s);
            if (sel.Count > 0) lines = sel;
        }
        TrySetClipboardText(string.Join(Environment.NewLine, lines));
    }

    private static void TrySetClipboardText(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text ?? string.Empty);
        }
        catch (Exception ex)
        {
            // Clipboard.SetText throws COMException intermittently (e.g. another process holds
            // the clipboard). We log and continue — losing the copy is annoying but never fatal.
            Debug.WriteLine($"[harness-log] clipboard set failed: {ex.Message}");
        }
    }

    /// <summary>D5: automatically collapse the right panel on narrow windows unless the user pinned it.</summary>
    internal void ApplyResponsiveLayout(double windowWidth)
    {
        if (rightPanelUserPinned) return;
        RightPanelWidth = windowWidth < 980
            ? new System.Windows.GridLength(0)
            : new System.Windows.GridLength(240);
    }

    public ObservableCollection<ModelChoice> ModelChoices { get; } = new();
    public ObservableCollection<JobUi> Jobs { get; } = new();
    public ObservableCollection<SubagentNodeUi> Subagents { get; } = new();
    public ObservableCollection<TodoItem> Todos { get; } = new();
    public ObservableCollection<QueuedInboxItem> QueueItems { get; } = new();

    // ---- D3 Plan / D5 permission chips (projections) ----
    /// <summary>True when the session's plan projection is mounted and active (D3).</summary>
    [ObservableProperty]
    private bool isPlanMode;

    /// <summary>True when a permissions projection is mounted (D5). Hides the chip row otherwise.</summary>
    [ObservableProperty]
    private bool hasPermissionSelect;

    /// <summary>Permission presets advertised by the projection; click a chip to stage `/permission <value>`.</summary>
    public ObservableCollection<PresetOption> PermissionOptions { get; } = new();

    /// <summary>Currently selected permission preset name (read-only label).</summary>
    [ObservableProperty]
    private string permissionCurrent = "";
    public ObservableCollection<SettingsEntry> SettingsEntries { get; } = new();
    public ObservableCollection<CredentialEntry> CredentialEntries { get; } = new();
    public ObservableCollection<DiscoveredModelView> DiscoveredModels { get; } = new();

    /// <summary>Provider id / base URL for model discovery.</summary>
    [ObservableProperty]
    private string discoverProvider = "deepseek";

    /// <summary>Ref name for the API-key input (e.g. "deepseek").</summary>
    [ObservableProperty]
    private string credentialRef = "deepseek";

    /// <summary>Credential value from the password box (write-only; never read back).</summary>
    [ObservableProperty]
    private string credentialValue = "";

    /// <summary>The currently selected model id (provider/model pair), if any.</summary>
    [ObservableProperty]
    private string? selectedModelId;

    /// <summary>Human-readable label for the active model.</summary>
    [ObservableProperty]
    private string modelLabel = Loc.Get("Model.NotSelected");

    /// <summary>
    /// Best-effort session stats line (P2-7) from the <c>sessionStats</c>/<c>tokenUsage</c>
    /// projections, e.g. "4 turns · 21 steps · 1.2k tok". Empty when the host does not push
    /// these projections (currently the case — see 02 C17). Displayed in the status bar only
    /// when non-empty, so the UI stays clean until the host adds the projection contract.
    /// </summary>
    [ObservableProperty]
    private string sessionStatsText = "";

    // C17 rich stats line: when the selected session's projections are present we drive a two-part
    // status-bar UI (context ring + detail chips) instead of the plain text fallback.
    [ObservableProperty]
    private bool hasRichStats;

    /// <summary>Context-ring fraction 0..1; 0 when unknown (hidden).</summary>
    [ObservableProperty]
    private double statsRingFraction;

    /// <summary>Ring label like "42%"; empty when no fraction.</summary>
    [ObservableProperty]
    private string statsRingText = "";

    /// <summary>Detail chips text: duration · TTFT · decode rate · total tokens.</summary>
    [ObservableProperty]
    private string statsDetailText = "";

    /// <summary>Precise context-ring tooltip, e.g. "42% · 4.2K / 10K tokens"; empty when the
    /// projection is absent.</summary>
    [ObservableProperty]
    private string statsRingTooltip = "";

    /// <summary>True when the session has a routable model selection (E3). False blocks the composer.</summary>
    [ObservableProperty]
    private bool isRoutable = true;

    /// <summary>Composer lock message shown when no route is available (E3).</summary>
    [ObservableProperty]
    private string routableMessage = "";

    /// <summary>
    /// Composer mode (P0-2): <see cref="ComposerMode.Idle"/> (no turn running) vs
    /// <see cref="ComposerMode.Running"/> (a turn is streaming). A steer briefly switches to
    /// <see cref="ComposerMode.Steering"/>. Drives the status badge and send/steer affordance.
    /// </summary>
    [ObservableProperty]
    private ComposerMode composerMode = ComposerMode.Idle;

    /// <summary>True while the selected session is streaming assistant chunks (呼吸指示).</summary>
    [ObservableProperty]
    private bool isStreaming;

    /// <summary>Badge label for the current composer mode (P0-2).</summary>
    public string ComposerModeText => ComposerMode switch
    {
        ComposerMode.Running => Loc.Get("Mode.Running"),
        ComposerMode.Steering => Loc.Get("Mode.Steering"),
        _ => Loc.Get("Mode.Idle"),
    };

    /// <summary>Send button caption: idle → send; running → enqueue.</summary>
    public string SendButtonText => ComposerMode == ComposerMode.Idle ? Loc.Get("Send.Send") : Loc.Get("Send.Enqueue");

    /// <summary>Steer is only meaningful while a turn is running (insert ahead of the queue).</summary>
    public bool CanSteer => ComposerMode != ComposerMode.Idle;

    partial void OnComposerModeChanged(ComposerMode value)
    {
        OnPropertyChanged(nameof(ComposerModeText));
        OnPropertyChanged(nameof(SendButtonText));
        OnPropertyChanged(nameof(CanSteer));
    }

    /// <summary>True while a prompt send is in flight (P0-4): disables send/steer to avoid double-submit.</summary>
    [ObservableProperty]
    private bool isSending;

    /// <summary>Transient status-bar notification text (P0-1); empty hides the notification bar.</summary>
    [ObservableProperty]
    private string notificationText = "";

    /// <summary>Notification severity (error/info/success); drives the bar's color.</summary>
    [ObservableProperty]
    private string notificationKind = "info";

    /// <summary>How long a notification stays visible before auto-clearing (P0-1).</summary>
    private const int NotificationDurationMs = 5000;

    /// <summary>CTS for cancelling a pending auto-clear when a newer notification replaces it.</summary>
    private CancellationTokenSource? _notificationCts;

    /// <summary>
    /// Raised after a <c>/</c> command is applied (P1-7) so the view can return keyboard focus
    /// to the composer input. The view (MainWindow) subscribes and calls <c>Focus()</c>.
    /// </summary>
    public event EventHandler? ComposerFocusRequested;

    public MainViewModel()
    {
        // 流式节流：Transcript 同步动作本身，合并高频 chunk。
        _uiThrottle = new UiThrottle(SyncFoldToUi);

        // Restore persisted preferences (harness directory, gateway URL) so the user doesn't
        // have to re-pick the checkout on every launch.
        HostUrl = _settings.HostUrl;
        HarnessDirectory = _settings.HarnessDirectory;
        BusyEnterAction = _settings.BusyEnterAction; // P1-8: restore the composer Enter-action preference.

        // P1-14: use the shared connection scope (single client + streams). Configure the URL,
        // then the client/service are available via ConnectionScope.Instance.
        ConnectionScope.Instance.SetBaseUrl(HostUrl);
        _interactions = new InteractionCoordinator(ConnectionScope.Instance.Client);

        // Subscribe to the shared downstream frame streams. Each window gets every frame but
        // filters by its own focus; the filter (step 3) keeps windows from cross-contaminating.
        // P1-fix: hold the disposable handles and Dispose() them when this VM goes out of scope,
        // otherwise the singleton keeps this VM reachable and continues to invoke its handlers.
        _subscriptions.Add(ConnectionScope.Instance.Subscribe(OnScopeMuxFrame));
        _subscriptions.Add(ConnectionScope.Instance.SubscribeHost(OnScopeHostFrame));
        _subscriptions.Add(ConnectionScope.Instance.SubscribeConnection(OnScopeConnectionStateChanged));

        // P1-14 step 4: a window opened after the connection is already established must pull its
        // own tree immediately (it won't receive a fresh connect event for an existing session).
        if (ConnectionScope.Instance.IsConnected)
        {
            IsConnected = true;
            ServiceRunning = true;
            _ = RefreshWorkspacesAsync();
            _ = RefreshSessionsAsync();
        }

        _ = RefreshHarnessStatusAsync();

        // A2/A3 monitoring hook: sample the fold's streaming telemetry every 500ms and log when
        // the deferred-optimization thresholds are approached (A2: high avg µs per append from
        // the O(n) text rebuild; A3: high chunks/s). This is observation only — it records the
        // trigger evidence (see os/16 E-group) rather than auto-enabling the optimizations.
        _streamingSampler = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            System.Windows.Threading.DispatcherPriority.Background,
            OnStreamingSamplerTick,
            System.Windows.Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher)
        { IsEnabled = true };
    }

    private readonly System.Windows.Threading.DispatcherTimer _streamingSampler;

    private void OnStreamingSamplerTick(object? sender, System.EventArgs e)
    {
        var s = _fold.TakeStreamingStats();
        if (s.ChunkCount == 0) return;
        double chunksPerSec = s.ChunkCount / 0.5;              // per 500ms sample
        if (s.AvgAppendMicros > 50_000)                         // ~50ms per append => O(n²) is biting
        {
            Logging.Log.Warn(string.Format(
                "A2-evidence: avg append {0:0}µs over {1} chunks, peak rows {2}",
                s.AvgAppendMicros, s.ChunkCount, s.PeakRows));
        }
        if (chunksPerSec > 30)                                  // A3: >30 deltas/s
        {
            Logging.Log.Warn(string.Format(
                "A3-evidence: {0:0}/s chunks, avg append {1:0}µs, peak rows {2}",
                chunksPerSec, s.AvgAppendMicros, s.PeakRows));
        }
    }

    /// <summary>
    /// P1-fix (event-leak): dispose every <see cref="ConnectionScope"/> subscription taken in
    /// <see cref="Attach"/>. Without this the singleton holds the VM reachable forever and the
    /// old window keeps mutating its (now detached) ObservableCollections. Safe to call multiple
    /// times; the inner <see cref="IDisposable"/> tokens self-null on Dispose.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var sub in _subscriptions)
        {
            try { sub.Dispose(); }
            catch { /* never let one bad subscriber block the rest */ }
        }
        _subscriptions.Clear();
    }

    partial void OnSelectedSessionIdChanged(string? value)
    {
        // Surface the selected session's title prominently in the content area.
        SelectedSessionTitle = value is null ? "" : FindSessionTitle(value);

        if (value is null) return;
        // P0-2: reset the composer badge for the newly selected session, seeding it from the
        // sidebar's last-known Running flag (the live turn/start–turn/end stream then refines it).
        ComposerMode = IsSessionRunning(value) ? ComposerMode.Running : ComposerMode.Idle;
        RefreshSessionStats(); // P2-7: re-read the stats projection for the newly selected session.
        _ = LoadHistoryAsync(value);
        // Load models + subagents when a session is selected (the original code only
        // loaded history, leaving the model picker empty until a manual refresh).
        _ = LoadModelsAsync();
        _ = LoadSubagentsAsync();
        _ = LoadCommandCatalogAsync();
    }

    /// <summary>
    /// Whether the given session id belongs to this window's tree (P1-14 step 3). A secondary
    /// window that focuses a single session only processes frames for the sessions it displays,
    /// so mux frames from other sessions never contaminate its fold/approval/jobs.
    /// </summary>
    /// <summary>Find the selected session's title from the sidebar tree, or an empty string
    /// (e.g. a session that is not (yet) in the local workspace tree).</summary>
    private string FindSessionTitle(string sessionId)
    {
        foreach (var ws in Workspaces)
        {
            foreach (var s in ws.Sessions)
            {
                if (s.Id == sessionId) return s.Title;
            }
        }
        return "";
    }

    private bool IsSessionInTree(string sessionId)
    {
        foreach (var ws in Workspaces)
        {
            foreach (var s in ws.Sessions)
            {
                if (s.Id == sessionId) return true;
            }
        }
        return false;
    }

    /// <summary>Whether the given session id is currently flagged running in the sidebar tree.</summary>
    private bool IsSessionRunning(string sessionId)
    {
        foreach (var ws in Workspaces)
        {
            foreach (var s in ws.Sessions)
            {
                if (s.Id == sessionId) return s.Running;
            }
        }
        return false;
    }

    /// <summary>
    /// Update a session row's waiting-flag in the sidebar tree (P1-2). The <see cref="SessionItem"/>
    /// is observable, so this flips the amber dot live. No-op when the session isn't in the tree
    /// (e.g. a cross-window/tab session); the next tree refresh reconciles it.
    /// </summary>
    private void SetSessionWaiting(string sessionId, bool waiting)
    {
        foreach (var ws in Workspaces)
        {
            foreach (var s in ws.Sessions)
            {
                if (s.Id == sessionId)
                {
                    s.Waiting = waiting;
                    return;
                }
            }
        }
    }
}
/// <summary>A session row in the sidebar list.</summary>
public sealed class SessionItem : ObservableObject
{
    private string _title = "";
    private bool _running;
    private bool _waiting;
    private bool _jobRunning;

    public string Id { get; set; } = "";

    /// <summary>The session display title (observable so the tree updates live).</summary>
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    /// <summary>True when the session's agent is actively running (blue dot).</summary>
    public bool Running
    {
        get => _running;
        set => SetProperty(ref _running, value);
    }

    /// <summary>True when the session is awaiting an interaction (amber dot).</summary>
    public bool Waiting
    {
        get => _waiting;
        set => SetProperty(ref _waiting, value);
    }

    /// <summary>True when the session has a running background job (green dot).</summary>
    public bool JobRunning
    {
        get => _jobRunning;
        set => SetProperty(ref _jobRunning, value);
    }

    /// <summary>The session's working directory; used by "open folder" (host.openPath).</summary>
    public string? Cwd { get; set; }

    /// <summary>True when the session is a blank placeholder (no turns yet); reusable by "new session".</summary>
    public bool Blank { get; set; }

    /// <summary>
    /// Last-update time as a short display string for the hover card (P1-1). Set from the
    /// wire <c>UpdatedAt</c> (epoch ms); empty when unknown.
    /// </summary>
    public string UpdatedAtText { get; set; } = "";

    /// <summary>
    /// Human-readable activity kind for the hover card (P1-1/P1-2), derived from the status
    /// dots with the same precedence: job running → green, agent running → blue, awaiting →
    /// amber, else idle.
    /// </summary>
    public string KindText => JobRunning ? Loc.Get("Job.Running")
        : Running ? Loc.Get("Job.Active")
        : Waiting ? Loc.Get("Job.Awaiting")
        : Loc.Get("Job.Idle");

    /// <summary>Culture-invariant status key so the hover card's color DataTrigger compares a
    /// stable enum instead of the localized <see cref="KindText"/>.</summary>
    public string KindKey => JobRunning ? "Running"
        : Running ? "Active"
        : Waiting ? "Awaiting"
        : "Idle";

    /// <summary>Tree icon (Segoe MDL2 Assets) shown before the session title (建议4).</summary>
    public string Icon { get; } = "\uE8F1"; // Chat / message bubble
}

/// <summary>A session.search hit: the session id, the match snippet, and the query that
/// produced it (so the UI can highlight the matched substring client-side, P2-13).</summary>
public sealed class SessionSearchHit
{
    public string SessionId { get; }

    public string Snippet { get; }

    public string Query { get; }

    /// <summary>Snippet split into highlighted/plain segments for inline emphasis.</summary>
    public IReadOnlyList<SnippetHighlighter.Segment> Segments { get; }

    public SessionSearchHit(string sessionId, string snippet, string query)
    {
        SessionId = sessionId;
        Snippet = snippet;
        Query = query;
        Segments = SnippetHighlighter.Highlight(snippet, query);
    }
}

/// <summary>A slash-command catalog entry (C12): merged from skill.list and agentPreset.list.</summary>
public sealed class CommandEntry
{
    public string Name { get; }

    public string Description { get; }

    public string Group { get; }

    public CommandEntry(string name, string description, string group)
    {
        Name = name;
        Description = description;
        Group = group;
    }
}

/// <summary>An active approval request carrying the frame and its stable rpcId.</summary>
public sealed record ApprovalRequest(ApprovalRequestedFrame Frame, RpcId RpcId);

/// <summary>A selectable model choice (provider/model pair) in the catalog.</summary>
public sealed record ModelChoice(string Id, string Label, string Provider, string Model)
{
    public override string ToString() => Label;
}

/// <summary>
/// Composer input-mode badge (P0-2). <see cref="Idle"/> = no turn running; <see cref="Running"/>
/// = a turn is streaming (steer may insert); <see cref="Steering"/> = a steer was just sent.
/// </summary>
public enum ComposerMode
{
    Idle,
    Running,
    Steering,
}

