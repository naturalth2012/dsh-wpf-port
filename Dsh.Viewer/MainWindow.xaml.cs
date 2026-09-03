using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Dsh.App;
using Dsh.Contract.Frames;
using Microsoft.Win32;

namespace Dsh.Viewer;

/// <summary>
/// Local-storage viewer shell (I-domain). L0 baseline replays <c>.jsonl</c>/<c>.json</c> files
/// through <see cref="SessionFold"/>; L1 (2026-08-24) adds SessionPathResolver / ZstdReader /
/// ChunkRowExpander for zstd + chunk-row decode. L2 (this file) groups the scanned sessions
/// into a workspace → session tree using <see cref="SessionPathResolver.Scan"/>, so the left
/// pane reflects the on-disk layout (cwd → sessionId) instead of a flat file list.
/// </summary>
public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly SessionFold _fold = new();
    private string _folderPath = string.Empty;
    private SessionNode? _selectedNode;
    private readonly ObservableCollection<WorkspaceNode> _workspaces = new();
    private readonly ObservableCollection<SessionFold.Row> _rows = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        Rows = _rows;
        Workspaces = _workspaces;
    }

    public ObservableCollection<WorkspaceNode> Workspaces { get; }
    public ObservableCollection<SessionFold.Row> Rows { get; }

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
            if (value is not null) LoadSelected(value);
        }
    }

    public int RowCount => _rows.Count;

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择会话存储目录" };
        if (dlg.ShowDialog() != true) return;
        FolderPath = dlg.FolderName;
        _workspaces.Clear();
        _rows.Clear();
        OnPropertyChanged(nameof(RowCount));

        // L2: group the scanned sessions into a workspace → session tree (cwd → sessionId).
        // SessionPathResolver.Scan handles the --<encoded-cwd>--/<encoded-sessionId>/ layout
        // and decodes each segment back to its original label.
        foreach (var group in SessionPathResolver.Scan(FolderPath)
                     .GroupBy(s => s.Workspace)
                     .OrderBy(g => g.Key))
        {
            var ws = new WorkspaceNode(group.Key);
            foreach (var s in group.OrderBy(x => x.SessionId))
            {
                ws.Sessions.Add(new SessionNode(
                    s.Path,
                    s.Workspace,
                    s.SessionId,
                    s.IsZstd,
                    s.IsZstd ? $"{s.SessionId} (zstd)" : s.SessionId));
            }
            _workspaces.Add(ws);
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
        if (!File.Exists(node.Path)) return;
        _fold.Reset();

        // L1 decode pipeline: transparent zstd, chunk-row expansion, then fold.
        bool isZstd = node.IsZstd;
        foreach (var line in ZstdReader.ReadLines(node.Path, isZstd))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonElement value;
            try { value = JsonSerializer.Deserialize<JsonElement>(line); }
            catch (JsonException) { continue; }
            foreach (var ev in ChunkRowExpander.Decode(value))
            {
                _fold.Fold(ev);
            }
        }
        foreach (var row in _fold.Rows) _rows.Add(row);
        OnPropertyChanged(nameof(RowCount));
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
public sealed record SessionNode(string Path, string Workspace, string SessionId, bool IsZstd, string DisplayName);
