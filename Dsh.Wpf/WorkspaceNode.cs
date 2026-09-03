using System.Collections.ObjectModel;

namespace Dsh.Wpf;

/// <summary>
/// A workspace tree node: groups its sessions under the workspace title. Matches the
/// sidebar TreeView (workspace → sessions) from `workspace.list` + `session.list`.
/// </summary>
public sealed class WorkspaceNode
{
    public string WorkspaceId { get; }

    public string Title { get; }

    public string Path { get; }

    /// <summary>Tree icon (Segoe MDL2 Assets) shown before the workspace title (建议4).</summary>
    public string Icon { get; } = "\uE8B7"; // Folder

    public ObservableCollection<SessionItem> Sessions { get; } = new();

    public WorkspaceNode(string workspaceId, string title, string path)
    {
        WorkspaceId = workspaceId;
        Title = title;
        Path = path;
    }
}
