using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.App;
using Dsh.App.Services;
using Localization = Dsh.App.Services.Localization;
using Microsoft.Win32;

namespace Dsh.Viewer;

/// <summary>
/// Local-storage viewer shell (I-domain). L0 baseline replays <c>.jsonl</c>/<c>.json</c> files
/// through <see cref="SessionFold"/>; L1 (2026-08-24) adds SessionPathResolver / ZstdReader /
/// ChunkRowExpander / ChunkRowExpander for zstd + chunk-row decode. L2 (this file) groups the scanned sessions
/// into a workspace → session tree using <see cref="SessionPathResolver.Scan"/>, so the left
/// pane reflects the on-disk layout (cwd → sessionId) instead of a flat file list. L3 (2026-09-03)
/// renders the right pane as a rich Surface view (<see cref="MessageRowControl"/> + Markdown via
/// the shared <see cref="Dsh.App.MarkdownRenderer"/>) with a per-item Raw detail panel.
/// </summary>
public partial class MainWindow : Window, INotifyPropertyChanged
{
    /// <summary>
    /// The fold for the currently-loaded session. Reused across loads in a single field would
    /// accumulate <c>Rows</c> (SessionFold.Reset() does not clear Rows), so LoadSelected replaces
    /// it with a fresh instance per session — mirroring how the online client creates one per
    /// session.
    /// </summary>
    private SessionFold _fold = new();
    private string _folderPath = string.Empty;
    private SessionNode? _selectedNode;
    private SurfaceItem? _selectedSurface;
    private readonly ObservableCollection<WorkspaceNode> _workspaces = new();
    private readonly ObservableCollection<SurfaceItem> _rows = new();
    private ICollectionView? _surfaceView;
    private string _searchText = "";
    private int _matchCount;

    public MainWindow()
    {
        InitializeComponent();
        // Restore the persisted UI language before binding (mirrors the main client).
        string persisted = AppSettings.Load().Language;
        Localization.SetLanguage(persisted);
        ApplyUiFont();
        Languages = Localization.Supported;
        _selectedLanguage = Languages.FirstOrDefault(l =>
            string.Equals(l.Code, persisted, StringComparison.OrdinalIgnoreCase)) ?? Languages[0];

        DataContext = this;
        Rows = _rows;
        Workspaces = _workspaces;
        _surfaceView = CollectionViewSource.GetDefaultView(_rows);
        _surfaceView.Filter = SurfaceFilter;
        SurfaceView = _surfaceView;
        RefreshHeader();
        TryAutoLoadDefaultFolder();
    }

    /// <summary>Supported languages for the toolbar ComboBox (from Localization.Supported).</summary>
    public IReadOnlyList<Localization.Language> Languages { get; private set; } = Array.Empty<Localization.Language>();

    private Localization.Language? _selectedLanguage;
    public Localization.Language? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (_selectedLanguage == value || value is null) return;
            _selectedLanguage = value;
            OnPropertyChanged(nameof(SelectedLanguage));
            Localization.SetLanguage(value.Code);
            new AppSettings { Language = value.Code }.Save();
            ApplyUiFont();
            RefreshHeader();
            BuildStatsText();
            RefreshFilter();
        }
    }

    private void ApplyUiFont()
    {
        FontFamily = new FontFamily(Localization.UiFont);
    }

    public ObservableCollection<WorkspaceNode> Workspaces { get; }
    public ObservableCollection<SurfaceItem> Rows { get; }

    /// <summary>Filtered view of <see cref="Rows"/> that the Surface ListBox binds to (search).</summary>
    public ICollectionView? SurfaceView { get; }

    /// <summary>Text used to filter the Surface view (S4a).</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            OnPropertyChanged(nameof(SearchText));
            RefreshFilter();
        }
    }

    /// <summary>Number of rows matching the current filter (drives a count label).</summary>
    public int MatchCount
    {
        get => _matchCount;
        private set
        {
            _matchCount = value;
            OnPropertyChanged(nameof(MatchCount));
            MatchText = Localization.Format("Viewer.MatchUnit", _matchCount);
        }
    }

    /// <summary>Localized match-count label, e.g. "{0} 匹配".</summary>
    public string MatchText { get; private set; } = "";

    // ── S4b statistics (computed once per loaded session from folded Rows + Trajectory) ───────

    public int StatMessages { get; private set; }
    public int StatToolCalls { get; private set; }
    public int StatToolErrors { get; private set; }
    public int StatTurnCount { get; private set; }
    public long StatInputTokens { get; private set; }
    public long StatOutputTokens { get; private set; }
    public string StatDurationText { get; private set; } = "";

    private void ComputeStats()
    {
        long msg = 0, tools = 0, toolErrors = 0, turns = 0;
        foreach (var item in _rows)
        {
            var r = item.Row;
            if (r.Role is "user" or "assistant") msg++;
            else if (r.Role == "tool") { tools++; if (r.Tool?.Status == "error") toolErrors++; }
            else if (r.Role == "error") toolErrors++;
            else if (r.Role == "turn") turns++;
        }
        StatMessages = (int)msg;
        StatToolCalls = (int)tools;
        StatToolErrors = (int)toolErrors;
        StatTurnCount = (int)turns;

        long inT = 0, outT = 0;
        long? firstTime = null, lastTime = null;
        foreach (var row in _fold.Rows)
        {
            if (row.Time is { } t)
            {
                firstTime ??= t;
                lastTime = t;
            }
        }
        foreach (var s in _fold.Trajectory)
        {
            inT += s.InputTokens ?? 0;
            outT += s.OutputTokens ?? 0;
        }
        StatInputTokens = inT;
        StatOutputTokens = outT;
        StatDurationText = firstTime is { } f && lastTime is { } l && l >= f
            ? FormatDuration(l - f) : "";

        OnPropertyChanged(nameof(StatMessages));
        OnPropertyChanged(nameof(StatToolCalls));
        OnPropertyChanged(nameof(StatToolErrors));
        OnPropertyChanged(nameof(StatTurnCount));
        OnPropertyChanged(nameof(StatInputTokens));
        OnPropertyChanged(nameof(StatOutputTokens));
        OnPropertyChanged(nameof(StatDurationText));
    }

    /// <summary>Build a filesystem-safe base name (title/short-id stripped of illegal chars).</summary>
    private static string MakeSafeFileName(SessionNode node)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string baseName = new string(node.DisplayName
            .Where(ch => !invalid.Contains(ch))
            .Select(ch => ch)
            .ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(baseName)) baseName = node.ShortId;
        return baseName.Length > 60 ? baseName[..60] : baseName;
    }

    private static string FormatDuration(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        if (ts.TotalMinutes < 1) return $"{ts.TotalSeconds:0}s";
        if (ts.TotalHours < 1) return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{(int)ts.TotalHours}h {(int)ts.Minutes}m";
    }

    /// <summary>Localized statistics line shown in the stats bar. Rebuilt on load and language switch.</summary>
    public string StatsLine { get; private set; } = "";

    private void BuildStatsText()
    {
        string sep = " · ";
        StatsLine = string.Join("",
            StatMessages, " ", Localization.Get("Viewer.StatsMsgs"), sep,
            StatToolCalls, " ", Localization.Get("Viewer.StatsTools"), sep,
            StatToolErrors, " ", Localization.Get("Viewer.StatsErrors"), sep,
            StatTurnCount, " ", Localization.Get("Viewer.StatsTurns"),
            "  |  ", Localization.Get("Viewer.TokenIn"), " ", StatInputTokens,
            " ", Localization.Get("Viewer.Tokens"), " · ", Localization.Get("Viewer.TokenOut"), " ",
            StatOutputTokens, " ", Localization.Get("Viewer.Tokens"),
            "  |  ", Localization.Get("Viewer.Duration"), " ", StatDurationText);
        OnPropertyChanged(nameof(StatsLine));
    }

    private void RefreshFilter()
    {
        _surfaceView?.Refresh();
        MatchCount = _surfaceView?.Cast<object>().Count() ?? 0;
        if (MatchLabel is not null)
        {
            MatchLabel.Visibility = string.IsNullOrWhiteSpace(_searchText)
                ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private bool SurfaceFilter(object obj)
    {
        if (obj is not SurfaceItem item) return false;
        if (string.IsNullOrWhiteSpace(_searchText)) return true;
        var q = _searchText.Trim();
        if (item.Role.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (item.Row.Text?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (item.Row.Reasoning?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (item.Row.Tool is { } t && ToolMatches(t, q)) return true;
        return false;
    }

    private static bool ToolMatches(SessionFold.ToolCallNode t, string q)
    {
        if (t.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (t.Arguments?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (t.Output?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (t.Children is not null) foreach (var c in t.Children) if (ToolMatches(c, q)) return true;
        return false;
    }

    public string FolderPath
    {
        get => _folderPath;
        private set { _folderPath = value; OnPropertyChanged(nameof(FolderPath)); }
    }

    public SessionNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            _selectedNode = value;
            OnPropertyChanged(nameof(SelectedNode));
            if (value is not null) { LoadSelected(value); RefreshHeader(); }
            else { RefreshHeader(); }
        }
    }

    public int RowCount => _rows.Count;

    /// <summary>Localized header line: "会话：{name} · {n} 行". Rebuilt on session load and language switch.</summary>
    public string HeaderLine { get; private set; } = "";

    private void RefreshHeader()
    {
        string label = Localization.Get("Viewer.Toolbar.Session");
        string name = SelectedNode?.DisplayName ?? "-";
        string rows = Localization.Format("Viewer.Toolbar.RowsUnit", RowCount);
        HeaderLine = $"{label}{name}  ·  {rows}";
        OnPropertyChanged(nameof(HeaderLine));
    }

    /// <summary>Selected Surface view item; drives the Raw detail panel (right).</summary>
    public SurfaceItem? SelectedSurface
    {
        get => _selectedSurface;
        set { _selectedSurface = value; OnPropertyChanged(nameof(SelectedSurface)); }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        // Open the picker at the currently loaded directory (auto-detected ~/.dsh/sessions or a
        // user-chosen folder) so users can browse from where they already are.
        var dlg = new OpenFolderDialog { Title = Localization.Get("Viewer.PickSessionDir") };
        if (!string.IsNullOrEmpty(FolderPath) && Directory.Exists(FolderPath))
        {
            // InitialDirectory opens the native picker already inside the current folder.
            dlg.InitialDirectory = FolderPath;
        }
        if (dlg.ShowDialog() != true) return;
        LoadFolderIntoTree(dlg.FolderName);
    }

    /// <summary>Populate the workspace/session tree from a directory. Used by both manual selection
    /// and the auto-detected default location (~/.dsh/sessions) so users don't have to click
    /// "Open folder" on every launch.</summary>
    private void LoadFolderIntoTree(string path)
    {
        FolderPath = path;
        _workspaces.Clear();
        _rows.Clear();
        OnPropertyChanged(nameof(RowCount));

        // L2: group the scanned sessions into a workspace → session tree (cwd → sessionId).
        // SessionPathResolver.Scan handles the --<encoded-cwd>--/<encoded-sessionId>/ layout
        // and decodes each segment back to its original label.
        foreach (var group in SessionPathResolver.Scan(path)
                     .GroupBy(s => s.Workspace)
                     .OrderBy(g => g.Key))
        {
            var ws = new WorkspaceNode(group.Key);
            foreach (var s in group.OrderBy(x => x.SessionId))
            {
                ws.Sessions.Add(new SessionNode(s.Path, s.Workspace, s.SessionId, s.IsZstd));
            }
            _workspaces.Add(ws);
        }
    }

    /// <summary>Attempt to auto-load the default deepseek-harness sessions directory
    /// (<c>%USERPROFILE%\.dsh\sessions</c>) so users don't have to click "Open folder"
    /// on every launch. Silent when the directory is missing.</summary>
    private void TryAutoLoadDefaultFolder()
    {
        string? envDir = Environment.GetEnvironmentVariable("DSH_HOME");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (string? candidate in new string?[]
        {
            envDir is null ? null : Path.Combine(envDir, "sessions"),
            Path.Combine(home, ".dsh", "sessions"),
        })
        {
            if (candidate is not null && Directory.Exists(candidate))
            {
                LoadFolderIntoTree(candidate);
                return;
            }
        }
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // Only session leaves are selectable; selecting a workspace node loads nothing.
        if (e.NewValue is SessionNode node) SelectedNode = node;
    }

    private void LoadSelected(SessionNode node)
    {
        _rows.Clear();
        if (!File.Exists(node.Path))
        {
            OnPropertyChanged(nameof(RowCount));
            RefreshFilter();
            return;
        }

        // One fresh SessionFold per session load: SessionFold.Rows is not cleared by Reset(),
        // so reusing a single instance across sessions would accumulate stale rows. This mirrors
        // how the online client instantiates a fold per session.
        var fold = new SessionFold();
        string? sessionTitle = null;
        try
        {
            // L1 decode pipeline: transparent zstd, chunk-row expansion, then fold.
            foreach (var line in ZstdReader.ReadLines(node.Path, node.IsZstd))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonElement value;
                try { value = JsonSerializer.Deserialize<JsonElement>(line); }
                catch (JsonException) { continue; }
                // Capture the real session title from a session/title event (SessionFold does not
                // retain it), so the tree + header can show the title instead of the session id.
                if (value.ValueKind == JsonValueKind.Object
                    && value.TryGetProperty("type", out var tp)
                    && tp.ValueKind == JsonValueKind.String
                    && tp.GetString() == "session/title"
                    && value.TryGetProperty("data", out var tdata)
                    && tdata.TryGetProperty("title", out var ttitle)
                    && ttitle.ValueKind == JsonValueKind.String)
                {
                    sessionTitle = ttitle.GetString();
                }
                try
                {
                    foreach (var ev in ChunkRowExpander.Decode(value))
                    {
                        fold.Fold(ev);
                    }
                }
                catch
                {
                    // A single malformed event must not abort the whole session load (os/06
                    // discipline): surface a non-fatal marker and continue with the next line.
                    fold.Rows.Add(new SessionFold.Row("error", Localization.Get("Viewer.DecodeSkip")));
                }
            }
            node.SetTitle(sessionTitle);
            // Flush any streaming assistant text that never got finalized by an assistant/message
            // frame. Real sessions deliver most text via text-chunks, so without this the last
            // assistant row stays empty.
            fold.MaterializePendingRow();
        }
        catch (Exception ex)
        {
            // A read/decompression failure must not leave a half-cleared, silent empty pane.
            fold.Rows.Add(new SessionFold.Row("error", Localization.Format("Viewer.ReadFail", ex.Message)));
        }

        _fold = fold;
        int idx = 0;
        foreach (var row in fold.Rows) _rows.Add(new SurfaceItem(row, idx++));
        OnPropertyChanged(nameof(RowCount));
        SelectedSurface = null;
        RefreshFilter();
        ComputeStats();
        RefreshHeader();
        BuildStatsText();
    }

    // ── Raw detail population (reliable, code-behind) ─────────────────────────────────────────

    private void SurfaceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedSurface = SurfaceList?.SelectedItem as SurfaceItem;
        if (SelectedSurface is not null)
        {
            if (RawBox is not null)
            {
                RawBox.Text = SelectedSurface.RawJson;
                RawExpander.IsExpanded = true;
            }
        }
        else if (RawBox is not null)
        {
            RawBox.Text = "";
        }
    }

    // ── S4c export handlers ────────────────────────────────────────────────────────────────────

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.ContextMenu is { } menu)
        {
            menu.PlacementTarget = b;
            menu.IsOpen = true;
        }
    }

    private void ExportMarkdown_Click(object sender, RoutedEventArgs e)
    {
        string caption = Localization.Get("Viewer.ExportMarkdownTitle");
        if (_rows.Count == 0 || _selectedNode is null)
        {
            MessageBox.Show(Localization.Get("Viewer.ExportNoSession"), caption,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = caption,
            FileName = MakeSafeFileName(_selectedNode) + ".md",
            DefaultExt = ".md",
            Filter = "Markdown (*.md)|*.md",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            TranscriptExporter.ExportMarkdown(_selectedNode.DisplayName, _rows.Select(r => r.Row), dlg.FileName);
            MessageBox.Show(Localization.Get("Viewer.ExportDone"), caption,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Localization.Format("Viewer.ExportFailed", ex.Message), caption,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportRawJsonl_Click(object sender, RoutedEventArgs e)
    {
        string caption = Localization.Get("Viewer.ExportJsonlTitle");
        if (_selectedNode is null)
        {
            MessageBox.Show(Localization.Get("Viewer.ExportNoSession"), caption,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = caption,
            FileName = _selectedNode.SessionId + ".jsonl",
            DefaultExt = ".jsonl",
            Filter = "JSON Lines (*.jsonl)|*.jsonl",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            TranscriptExporter.ExportRawJsonl(_selectedNode.Path, _selectedNode.IsZstd, dlg.FileName);
            MessageBox.Show(Localization.Get("Viewer.ExportJsonlDone"), caption,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Localization.Format("Viewer.ExportFailed", ex.Message), caption,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One decoded workspace node in the L2 tree (groups sessions under a decoded cwd).</summary>
public sealed class WorkspaceNode
{
    public WorkspaceNode(string label) => Label = label;

    public string Label { get; }

    public ObservableCollection<SessionNode> Sessions { get; } = new();
}

/// <summary>One decoded session node in the L2 tree (points at the on-disk session file).</summary>
public sealed partial class SessionNode : ObservableObject
{
    public SessionNode(string path, string workspace, string sessionId, bool isZstd)
    {
        Path = path;
        Workspace = workspace;
        SessionId = sessionId;
        IsZstd = isZstd;
        _displayName = ShortId;
    }

    public string Path { get; }
    public string Workspace { get; }
    public string SessionId { get; }
    public bool IsZstd { get; }

    /// <summary>
    /// A compact, unique-per-session label (e.g. <c>session-0494275d</c>). The first 16 chars
    /// include the shared <c>session-</c> prefix (8) and the unique GUID segment (8); this avoids the
    /// previous bug where every un-loaded session showed the identical <c>session-</c> short id,
    /// making the tree appear to "interleave" identical rows with titled ones.
    /// </summary>
    public string ShortId => SessionId.Length > 16 ? SessionId[..16] : SessionId;

    [ObservableProperty]
    private string _displayName;

    /// <summary>Set from a session/title event discovered during load; refreshes the tree label.</summary>
    public void SetTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        DisplayName = title.Trim();
    }
}
