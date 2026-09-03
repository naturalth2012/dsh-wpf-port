using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Dsh.App;

namespace Dsh.Wpf;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _allowClose;
    private static int _windowCounter;
    private GlobalHotKey? _hotKeys;
    private string? _lastModelSelection;

    public MainWindow()
    {
        InitializeComponent();
        // Sync the OS title bar with the in-app theme once the HWND is created.
        // SourceInitialized fires before Loaded; deferred call guarantees DWM success.
        SourceInitialized += (_, _) => TitleBarDark.Apply(this,
            ThemeSettings.Load().Theme?.Equals("dark", StringComparison.OrdinalIgnoreCase) ?? false);
        // P2-10 fix (2026-08-30): global hotkeys MUST be registered from SourceInitialized, not
        // from the constructor. In the constructor the window's HWND does not exist yet, so
        // WindowInteropHelper.Handle is IntPtr.Zero and HwndSource.FromHwnd(0) threw
        // "ArgumentException: 零的 Hwnd 无效" on every single startup (an unavoidable UI-thread
        // exception that also left the hotkeys silently unregistered).
        SourceInitialized += (_, _) => InitializeGlobalHotKeys();
        // Self-size to the work area so the fixed 1080x720 default never overflows a
        // smaller screen and a large screen gets a proportionally bigger window.
        var work = SystemParameters.WorkArea;
        if (work.Width > 0 && work.Height > 0)
        {
            Width = System.Math.Min(1080, work.Width * 0.92);
            Height = System.Math.Min(720, work.Height * 0.92);
        }
        _viewModel = new MainViewModel();
        // P1-14 step 3: the first window is "main" (owns the shared workspace tree); subsequent
        // windows are secondary (focus a single session, ignore workspace mutations).
        _viewModel.IsMainWindow = Interlocked.Increment(ref _windowCounter) == 1;
        DataContext = _viewModel;
        // Bug-A2 fix: hook so the view-model can force a TextBox → InputText flush
        // before SendCommand snapshots the composer text. Without this, KeyBinding.Enter
        // runs SendAsync() before UpdateSourceTrigger has propagated the latest Text.
        ComposerBridge.FlushComposerToInputText = FlushComposerBinding;
        // D3/D5: a chip staged a slash command into the composer — give it focus so Enter sends it.
        _viewModel.FocusComposer = () => ComposerBox?.Focus();
        // D5: responsive collapse of the right helper panel on narrow windows.
        SizeChanged += (_, _) => _viewModel.ApplyResponsiveLayout(ActualWidth);
        _viewModel.ApplyResponsiveLayout(ActualWidth);
        Closing += OnClosing;
        SessionTree.SelectedItemChanged += OnSessionTreeSelectionChanged;
        KeyDown += OnWindowKeyDown;
        // K4: apply the language-driven UI font fallback chain. The window root's XAML FontFamily
        // is a static literal, so we re-point it here (and on language change via the VM) so CJK
        // vs Latin faces follow the active language.
        _viewModel.ApplyUiFont();
        _viewModel.ComposerFocusRequested += (_, _) =>
        {
            // P1-7: after a / command is applied, return keyboard focus to the composer input.
            if (ComposerBox is { IsEnabled: true })
            {
                ComposerBox.Focus();
                ComposerBox.CaretIndex = ComposerBox.Text.Length;
            }
        };
        // P3: up-scroll lazy-loading of older history. Hook the transcript ListBox's internal
        // ScrollViewer; when the user scrolls to the top, request the older page (if any).
        Loaded += OnLoaded_ConnectHistoryScrolling;
    }

    /// <summary>
    /// P2-10: register the system-wide hotkeys. Called from <c>SourceInitialized</c>, which is the
    /// earliest point where the window's HWND exists (creating a GlobalHotKey any earlier threw
    /// "零的 Hwnd 无效"). Guarded so a repeated SourceInitialized cannot double-register.
    /// </summary>
    private void InitializeGlobalHotKeys()
    {
        if (_hotKeys is not null) return;
        // Main window only: secondary windows must not fight over the same hotkey ids.
        if (!_viewModel.IsMainWindow) return;
        try
        {
            _hotKeys = new GlobalHotKey(this);
            _hotKeys.RegisterHotKey(System.Windows.Input.Key.N, () =>
                _viewModel.NewSessionCommand.Execute(null));
            _hotKeys.RegisterHotKey(System.Windows.Input.Key.H, () =>
            {
                Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                Topmost = true; Topmost = false; // bring to front over other apps.
            });
        }
        catch (Exception ex)
        {
            // Hotkeys are a convenience; never let a registration failure break startup.
            Dsh.Wpf.Logging.Log.Warn($"Global hotkey registration failed: {ex.Message}");
        }
    }

    private void OnLoaded_ConnectHistoryScrolling(object? sender, RoutedEventArgs e)
    {
        if (TranscriptList is null) return;
        TranscriptList.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnTranscriptScrollChanged), handledEventsToo: true);
        _viewModel.ScrollToAnchorRequested += anchor =>
        {
            if (anchor is not null && TranscriptList is not null)
            {
                TranscriptList.ScrollIntoView(anchor);
            }
        };
    }

    /// <summary>
    /// Double-click a transcript line to open its FULL content in a separate window.
    /// <para>
    /// Motivation (2026-08-31): with a long session the in-place list can fail to materialize the
    /// last rows at the tail of the viewport (WPF virtualization measure bug — see the
    /// "Destination array was not long enough" ArgumentException in dsh-client.log). Rather than
    /// fight the virtualization stack, this gives the user a guaranteed way to read any message:
    /// a modal window renders the content with its own ScrollViewer, completely outside the
    /// virtualized ListBox, so nothing can be clipped or left un-realized.
    /// </para>
    /// Renders via WebView2 (HtmlPreviewWindow) so Markdown, code fences and tables all look the
    /// same as they do inline.
    /// </summary>
    private void TranscriptList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Fully qualify ListBox: the project also references System.Windows.Forms, which has its own
        // ListBox type.
        if (sender is not System.Windows.Controls.ListBox lb) return;
        if (lb.SelectedItem is not ChatEntry entry) return;

        // Build a plain-text/Markdown document: role header + metadata, then the BODY first, and
        // the reasoning (if any) at the very end, labelled — see the content-order note below.
        // A metadata line is added because the window title is deliberately short
        // (see BuildMessageSummary) — the full details that don't fit in a title bar go here.
        var sb = new StringBuilder();
        sb.Append("# ").Append(RoleLabel(entry.Role)).AppendLine();
        sb.AppendLine();

        var meta = new List<string>();
        if (entry.Tool is { } metaTool)
        {
            meta.Add($"`{metaTool.Name}` ({metaTool.Status})");
        }
        if (!string.IsNullOrWhiteSpace(entry.MessageId))
        {
            meta.Add($"id={entry.MessageId}");
        }
        if (entry.Time is { } t && t > 0)
        {
            var stamp = FormatEpochTime(t);
            if (stamp.Length > 0) meta.Add(stamp);   // omit implausible epoch values entirely
        }
        if (meta.Count > 0)
        {
            sb.Append('>').Append(' ').AppendLine(string.Join(" · ", meta));
            sb.AppendLine();
        }

        // ── Content order (2026-09-01 fix) ──────────────────────────────────────────────────
        // The first version put Reasoning BEFORE the body. The inline transcript renders the
        // reasoning collapsed and the body visible, so users read the body first; the popup's
        // first screen being hundreds of lines of thinking-draft quotes looked like the popup
        // was showing DIFFERENT content than what the user clicked on (reported as "弹出框显示
        // 内容与源数据不一致"). Body/tool output must come first; the reasoning goes to the
        // END, clearly labelled as the model's internal draft rather than the final reply.
        if (entry.Tool is { } tool)
        {
            sb.AppendLine($"`{tool.Name}` — {tool.Status}");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(tool.Output))
            {
                sb.AppendLine("```");
                sb.AppendLine(tool.Output);
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }
        if (!string.IsNullOrWhiteSpace(entry.Text))
        {
            sb.AppendLine(entry.Text);
        }

        if (!string.IsNullOrWhiteSpace(entry.Reasoning))
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.Append("## ").AppendLine(
                Dsh.App.Services.Localization.Get("Msg.ReasoningSection") ?? "Reasoning");
            sb.AppendLine();
            sb.AppendLine("> " + (Dsh.App.Services.Localization.Get("Msg.ReasoningNote") ??
                "The model's internal thinking draft — not part of the final reply."));
            sb.AppendLine();
            foreach (var line in entry.Reasoning.Replace("\r\n", "\n").Split('\n'))
            {
                sb.Append("> ").AppendLine(line);
            }
        }

        if (sb.Length == 0) sb.AppendLine("_(empty)_");

        // The title carries identity, not just the role: two Assistant messages in the same
        // session previously produced identical titles ("详情 — Assistant"), which made several
        // open detail windows indistinguishable.
        var title = string.Format(
            Dsh.App.Services.Localization.Get("Msg.DetailTitle") ?? "{0}",
            BuildMessageSummary(entry));
        HtmlPreviewWindow.OpenMarkdown(title, sb.ToString(), this);
    }

    /// <summary>
    /// Short, single-line identity for a transcript entry, used as the detail window title:
    /// <c>角色[ · 工具名 (状态)] · 正文预览</c>.
    /// <para>
    /// The role alone is not identifying (a long session has many Assistant messages), so a
    /// one-line preview of the body is appended. For tool rows the tool name + status are used
    /// instead of the body, whose text is often empty or a raw arguments JSON blob.
    /// </para>
    /// </summary>
    private const int TitlePreviewChars = 50;

    private static string BuildMessageSummary(ChatEntry entry)
    {
        var role = RoleLabel(entry.Role);

        // Tool rows: prefer the tool identity — the body is usually arguments JSON, which reads
        // poorly in a title.
        if (entry.Tool is { } tool && !string.IsNullOrWhiteSpace(tool.Name))
        {
            var toolPart = $"{role} · {tool.Name}";
            if (!string.IsNullOrWhiteSpace(tool.Status)) toolPart += $" ({tool.Status})";
            return toolPart;
        }

        var preview = TrajectoryLedger.ToSingleLine(entry.Text);
        if (preview.Length == 0) preview = TrajectoryLedger.ToSingleLine(entry.Reasoning);
        if (preview.Length == 0) return role;
        return $"{role} · {TrajectoryLedger.TruncateTo(preview, TitlePreviewChars)}";
    }

    /// <summary>
    /// Format an epoch-millisecond timestamp as <c>HH:mm</c>. The metadata block needs a wall-clock
    /// time; <c>ChatEntry.Time</c> is epoch ms (the inline XAML shows it through
    /// <c>TimestampConverter</c>, which is epoch-based), so this helper matches that reading.
    /// </summary>
    private static string FormatEpochTime(long epochMs)
    {
        try
        {
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).ToLocalTime();
            return dt.ToString("HH:mm");
        }
        catch (ArgumentOutOfRangeException)
        {
            // Not a plausible epoch value (see the Time-semantics note in the commit message) —
            // show nothing rather than a misleading timestamp.
            return "";
        }
    }

    /// <summary>
    /// Double-click a trajectory ledger row to open its full detail in a separate window
    /// (same rationale as <see cref="TranscriptList_MouseDoubleClick"/>). Shows the step's
    /// summary, timing block and raw payload when the host provided one.
    /// </summary>
    private void TrajectoryRows_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox lb) return;
        if (lb.SelectedItem is not TrajectoryRow row) return;
        if (row.Step is not { } step) return;   // group header — nothing to show

        var sb = new StringBuilder();
        sb.Append("# ").Append(TrajectoryLedger.KindLabel(step.Kind)).AppendLine();
        sb.AppendLine();

        // Metadata line: everything that identifies this step. The window title is deliberately
        // short, so the complete set of identifiers lives here where there is room for it.
        var stepMeta = new List<string>
        {
            $"#{step.Index + 1}/{_viewModel.Trajectory.Count}",
        };
        if (step.TurnIndex is { } mTurn) stepMeta.Add($"Turn {mTurn}");
        if (step.RequestIndex is { } mReq) stepMeta.Add($"Request #{mReq}");
        if (step.DurationMs is { } mDur) stepMeta.Add(TrajectoryLedger.FormatDuration(mDur));
        if (step.TtftMs is { } mTtft) stepMeta.Add($"TTFT {TrajectoryLedger.FormatDuration(mTtft)}");
        if (step.IsError) stepMeta.Add("**ERROR**");
        if (step.CallId is { Length: > 0 } mCall) stepMeta.Add($"call={mCall}");
        // Token totals when the host reported them (no fabricated zeros).
        long?[] tokens = { step.InputTokens, step.OutputTokens, step.ThinkTokens };
        if (tokens.Any(v => v.HasValue))
        {
            var parts = new List<string>();
            if (step.InputTokens is { } ti) parts.Add($"in {ti}");
            if (step.OutputTokens is { } to) parts.Add($"out {to}");
            if (step.ThinkTokens is { } tt) parts.Add($"think {tt}");
            stepMeta.Add(string.Join(" · ", parts));
        }
        sb.Append('>').Append(' ').AppendLine(string.Join(" · ", stepMeta));
        sb.AppendLine();

        sb.AppendLine(_viewModel.TrajectoryDetailSummary);
        sb.AppendLine();
        sb.AppendLine("## Timing");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(_viewModel.TrajectoryDetailTiming);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Payload");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(_viewModel.TrajectoryDetailPayload);
        sb.AppendLine("```");

        // The title previously had no identifying information at all ("步骤详情" for every step),
        // so several open detail windows were indistinguishable.
        var title = string.Format(
            Dsh.App.Services.Localization.Get("Tr.DetailTitle") ?? "{0}",
            BuildStepSummary(step, _viewModel.Trajectory.Count));
        HtmlPreviewWindow.OpenMarkdown(title, sb.ToString(), this);
    }

    /// <summary>
    /// Short, single-line identity for a trajectory step, used as the detail window title:
    /// <c>KIND · Turn N[ · Request #M] · #index/total · 预览或耗时</c>.
    /// <para>
    /// Falls back to the duration when the step has no meaningful text (common for compacted /
    /// turn-boundary steps), so the title is never empty of information.
    /// </para>
    /// </summary>
    private static string BuildStepSummary(SessionFold.TrajectoryStep step, int totalSteps)
    {
        var parts = new List<string> { TrajectoryLedger.KindLabel(step.Kind) };
        if (step.TurnIndex is { } turn) parts.Add($"Turn {turn}");
        if (step.RequestIndex is { } req) parts.Add($"Request #{req}");
        parts.Add(totalSteps > 0 ? $"#{step.Index + 1}/{totalSteps}" : $"#{step.Index + 1}");
        if (step.IsError) parts.Add("ERROR");

        var preview = TrajectoryLedger.ToSingleLine(step.Text);
        if (preview.Length > 0)
        {
            parts.Add(TrajectoryLedger.TruncateTo(preview, TitlePreviewChars));
        }
        else if (step.DurationMs is { } dur)
        {
            parts.Add(TrajectoryLedger.FormatDuration(dur));
        }
        return string.Join(" · ", parts);
    }

    private static string RoleLabel(string role) => role switch
    {
        "user" => "User",
        "assistant" => "Assistant",
        "tool" => "Tool",
        "error" => "Error",
        _ => role,
    };

    /// <summary>Auto-load the Settings tab the first time the user opens it.
    /// Settings is request-driven (a settings.describe RPC), unlike the other tabs which are
    /// host-push driven, so it stays empty until someone asks for it. Previously that meant the
    /// panel looked broken until the user noticed the Refresh button. This fires once only —
    /// subsequent loads are explicit (Refresh), so we never fight a manual reload or spam RPCs.
    /// </summary>
    private bool _settingsAutoLoaded;

    /// <summary>
    /// Commit a splitter drag: hand the resulting column width to the view model, which clamps
    /// and persists it.
    /// <para>
    /// Persisting on DragCompleted rather than continuously is deliberate — every save is a JSON
    /// serialize plus a file write, and doing that on each mouse-move would mean hundreds of
    /// synchronous disk writes per drag (exactly the class of bug that froze this app before).
    /// </para>
    /// </summary>
    private void RightPanelSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        // RightPanelWidth is updated live by TwoWay binding during the drag; this only clamps
        // the final value and writes it to disk.
        _viewModel.SetRightPanelWidthFromDrag(_viewModel.RightPanelWidth.Value);
    }

    private void RightPanelTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingsAutoLoaded) return;
        // Fully qualify: System.Windows.Forms is also referenced and has its own TabControl/TabItem.
        if (sender is not System.Windows.Controls.TabControl tabs) return;
        if (tabs.SelectedItem is not System.Windows.Controls.TabItem item) return;
        // Identify the Settings tab by its x:Uid-free header text rather than by index, so the
        // auto-load keeps working if the tab order changes.
        if (item.Header is not string header) return;
        if (!string.Equals(header, Dsh.App.Services.Localization.Get("Tab.Settings"), StringComparison.Ordinal)) return;

        _settingsAutoLoaded = true;
        if (_viewModel.LoadSettingsCommand.CanExecute(null))
        {
            _viewModel.LoadSettingsCommand.Execute(null);
        }
    }

    /// <summary>When the transcript scrolls to (or past) the top and more history exists, ask
    /// the view-model to load the older page. The VM guards re-entry and the hasMore flag, so
    /// repeated events while a load is in flight are ignored; ScrollIntoView(anchor) after the
    /// head-insert keeps the view anchored so this does not fire again immediately.</summary>
    private void OnTranscriptScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Only react when the user has reached the very top of a scrollable (overflowing) list,
        // and no history load (tail or loadOlder) is in flight. While IsLoadingHistory is true,
        // the head-insert re-layout and the anchor ScrollIntoView fire further ScrollChanged
        // events; without this guard they would re-trigger LoadOlderHistory (flag reset races the
        // async layout), causing the loadOlder→scroll→loadOlder re-entry loop that freezes the UI.
        if (e.VerticalOffset <= 0 && e.ExtentHeight > e.ViewportHeight && !_viewModel.IsLoadingHistory)
        {
            _viewModel.LoadOlderHistoryCommand.Execute(null);
        }
    }

    /// <summary>Allows a genuine quit (tray Exit / Ctrl+Q) to pass OnClosing.</summary>
    public void AllowClose() => _allowClose = true;

    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Q
            && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control)
        {
            ((App)System.Windows.Application.Current).QuitFromShortcut();
            e.Handled = true;
        }
    }

    private void OnSessionTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // Fallback path: when the TreeView reports a selection change, recover the real
        // SessionItem. WPF's TreeView nests sessions under workspace nodes, so e.NewValue is
        // frequently the TreeViewItem *container* (or a WorkspaceNode) rather than the
        // SessionItem DataContext — TryGetSessionItem walks that down. Clicking a session row
        // is also handled directly in PreviewMouseLeftButtonDown (the more reliable path).
        if (TryGetSessionItem(e.NewValue, out var session) && session is not null)
        {
            _viewModel.SelectedSessionId = session.Id;
        }
    }

    private static bool TryGetSessionItem(object? value, out SessionItem? session)
    {
        session = null;
        if (value is SessionItem direct)
        {
            session = direct;
            return true;
        }
        // TreeViewItem container: its DataContext is the data object (SessionItem or WorkspaceNode).
        if (value is System.Windows.Controls.TreeViewItem tvi)
        {
            if (tvi.DataContext is SessionItem ctxSession)
            {
                session = ctxSession;
                return true;
            }
            // Wrong item selected (e.g. a workspace node); do not switch the active session.
            return false;
        }
        return false;
    }

    /// <summary>
    /// Restore the TreeView's visual selection to the active session after the workspace tree
    /// has been rebuilt (e.g. by RefreshWorkspacesAsync). Without this, the logical
    /// SelectedSessionId stays correct but the TreeView loses its SelectedItem — so the
    /// highlighted row and the loaded chat pane disagree (a silent "blank history" symptom).
    /// </summary>
    public void ReselectSession(string? sessionId)
    {
        if (sessionId is null) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // Re-query containers after the tree has rendered the new items.
            var node = _viewModel.Workspaces
                .SelectMany(w => w.Sessions)
                .FirstOrDefault(s => s.Id == sessionId);
            if (node is null) return;
            var container = SessionTree.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem
                ?? FindContainerRecursive(SessionTree, node);
            if (container is not null)
            {
                container.IsSelected = true;
            }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static TreeViewItem? FindContainerRecursive(ItemsControl parent, object item)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem direct)
            return direct;
        foreach (var child in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(child) is TreeViewItem childContainer)
            {
                var found = FindContainerRecursive(childContainer, item);
                if (found is not null) return found;
            }
        }
        return null;
    }

    private void CredentialPassword_Changed(object sender, RoutedEventArgs e)
    {
        // WPF PasswordBox never binds; forward the value to the view model (write-only).
        _viewModel.CredentialValue = ((PasswordBox)sender).Password;
    }

    /// <summary>
    /// Open a second window (P1-14 step 4). It shares <see cref="ConnectionScope.Instance"/> so
    /// no new connection is made; the window is "secondary" (not main), so it ignores workspace
    /// mutations and only shows its own focused session. Same URL as the primary window.
    /// </summary>
    private void NewWindow_Click(object sender, RoutedEventArgs e)
    {
        var win = new MainWindow { Owner = this };
        win.Show();
    }

    /// <summary>
    /// Open the non-modal settings window (U5). It reuses this window's ViewModel so
    /// theme/language/model changes apply instantly; Owner keeps it above the main window
    /// without blocking.
    /// </summary>
    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow
        {
            Owner = this,
            DataContext = _viewModel,
        };
        win.Show();
    }

    /// <summary>
    /// Configure the connection: edit the harness HostUrl in a small dialog (the toolbar shows it
    /// read-only like a browser address bar), then connect. Reuses InputDialog + ConnectCommand.
    /// </summary>
    private void ConfigureConnection_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new InputDialog(
            Dsh.App.Services.Localization.Get("Connect.EditUrlTitle"),
            Dsh.App.Services.Localization.Get("Connect.EditUrlPrompt"),
            _viewModel.HostUrl ?? "http://127.0.0.1:3080")
        {
            Owner = this,
            OkButtonText = Dsh.App.Services.Localization.Get("Connect.Edit"),
        };
        var result = dialog.ShowDialog();
        if (result == true && !string.IsNullOrWhiteSpace(dialog.Text))
        {
            _viewModel.HostUrl = dialog.Text.Trim();
            _viewModel.ConnectCommand.Execute(null);
        }
    }

    /// <summary>
    /// Top toolbar model ComboBox: apply the selection to the active session (B4). Guarded
    /// against the auto-fire during ItemsSource/SelectedValue population so initialization
    /// does not trigger a spurious switch.
    /// </summary>
    private void ModelComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Skip while the drop-down is still being populated from ModelChoices.
        if (_viewModel.ModelChoices.Count == 0) return;
        if (e.AddedItems.Count == 0) return;
        // Avoid reacting to programmatic re-selection of the same item.
        if (_lastModelSelection == _viewModel.SelectedModelId) return;
        _lastModelSelection = _viewModel.SelectedModelId;
        _viewModel.SelectModelCommand.Execute(null);
    }

    /// <summary>
    /// Composer Ctrl+V with an image on the clipboard attaches it to the prompt instead of
    /// pasting a bitmap as text (P2-14).
    /// </summary>
    private void Composer_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.V && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            if (System.Windows.Clipboard.ContainsImage())
            {
                _viewModel.AddPastedClipboardImage();
                e.Handled = true;
            }
        }
    }

    private object? _dragSource;

    /// <summary>
    /// Begin a tree drag (P2-1) by capturing the source node under the mouse. Only session and
    /// workspace rows are draggable; other areas are ignored.
    /// </summary>
    private void SessionTree_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragSource = null;
        var item = FindAncestor<System.Windows.Controls.TreeViewItem>((DependencyObject)e.OriginalSource);
        if (item?.DataContext is SessionItem or WorkspaceNode)
        {
            _dragSource = item.DataContext;
        }
        // Drive the active session directly from the clicked row's DataContext. This is the
        // most reliable selection path: WPF's SelectedItemChanged reports the TreeViewItem
        // container (or the parent WorkspaceNode) for nested templates, which can leave the
        // logical selection stuck on the previously auto-selected session. Reading the real
        // SessionItem here guarantees a click on "上海初二" actually loads that session.
        if (item?.DataContext is SessionItem clicked)
        {
            if (_viewModel.SelectedSessionId != clicked.Id)
            {
                _viewModel.SelectedSessionId = clicked.Id;
            }
        }
    }

    /// <summary>Enable copy-drop only when a draggable node is held (P2-1).</summary>
    private void SessionTree_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = _dragSource is not null ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Perform a workspace/session reorder on drop (P2-1).</summary>
    private void SessionTree_Drop(object sender, System.Windows.DragEventArgs e)
    {
        e.Handled = true;
        if (_dragSource is null) return;
        // Resolve the drop target from the mouse position.
        var targetItem = FindAncestor<System.Windows.Controls.TreeViewItem>((DependencyObject)e.OriginalSource);
        object? target = targetItem?.DataContext;

        if (_dragSource is SessionItem srcSession && target is SessionItem targetSession)
        {
            // Reorder within the same workspace (the workspace is the source session's parent).
            var workspace = FindWorkspaceOfSession(srcSession.Id);
            if (workspace is not null)
            {
                _viewModel.ReorderSessionCommand.Execute(
                    new MainViewModel.ReorderTarget(srcSession.Id, targetSession.Id, workspace.WorkspaceId));
            }
        }
        else if (_dragSource is WorkspaceNode srcWorkspace)
        {
            string? beforeId = target is WorkspaceNode tw ? tw.WorkspaceId : null;
            _viewModel.ReorderWorkspaceCommand.Execute(
                new MainViewModel.ReorderTarget(srcWorkspace.WorkspaceId, beforeId));
        }
        _dragSource = null;
    }

    private WorkspaceNode? FindWorkspaceOfSession(string sessionId)
    {
        foreach (var ws in _viewModel.Workspaces)
        {
            if (ws.Sessions.Any(s => s.Id == sessionId)) return ws;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    /// <summary>Accept image files dragged onto the window (P2-14).</summary>
    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Handle image files dropped onto the window (P2-14).</summary>
    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            && e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files)
        {
            _viewModel.AddImageFiles(files);
        }
        e.Handled = true;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            // The X button minimizes to the tray instead of closing; the tray's Exit menu
            // or Ctrl+Q truly shuts the app down. _allowClose lets a genuine quit through.
            e.Cancel = true;
            Hide();
            return;
        }
        // P1-14: detach this window from the shared scope; the stream loop stops only when the
        // last window detaches (App.OnExit also calls Detach as a final safety net).
        // P1-fix: dispose the VM's scope subscriptions BEFORE Detach so the singleton stops
        // invoking this window's handlers the moment the close begins.
        (DataContext as MainViewModel)?.Dispose();
        ConnectionScope.Instance.Detach();
        _hotKeys?.Dispose(); // P2-10: unregister global hotkeys on a genuine close.
    }

    /// <summary>
    /// Force the composer's TextBox to write its current Text back into the bound
    /// <c>InputText</c> property. Without this, <c>KeyBinding.Enter</c> can invoke
    /// <c>SendCommand</c> before <c>UpdateSourceTrigger=PropertyChanged</c> has flushed
    /// the most recent keystroke, leaving the optimistic user row text empty.
    /// </summary>
    private void FlushComposerBinding()
    {
        if (ComposerBox is null) return;
        var expr = BindingOperations.GetBindingExpression(ComposerBox, System.Windows.Controls.TextBox.TextProperty);
        expr?.UpdateSource();
    }
}
