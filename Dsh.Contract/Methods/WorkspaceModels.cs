using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>One workspace view, mirroring <c>workspaceViewSchema</c>.</summary>
public sealed record WorkspaceView
{
    [JsonPropertyName("workspaceId")]
    public required string WorkspaceId { get; init; }

    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("sessionIds")]
    public required string[] SessionIds { get; init; }

    [JsonPropertyName("createdAt")]
    public required string CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public required string UpdatedAt { get; init; }
}

/// <summary>Value of <c>workspace.list</c>, mirroring <c>workspaceListValueSchema</c>.</summary>
public sealed record WorkspaceListResponse
{
    [JsonPropertyName("items")]
    public required WorkspaceView[] Items { get; init; }

    [JsonPropertyName("archivedSessionIds")]
    public string[] ArchivedSessionIds { get; init; } = [];
}

/// <summary>Alias of the full <c>workspace.create</c> value slot: the adopted workspace view
/// plus whether a new registry entry was created (vs. adopting an already-registered path).</summary>
public sealed record WorkspaceCreateResult
{
    [JsonPropertyName("workspace")]
    public required WorkspaceView Workspace { get; init; }

    [JsonPropertyName("created")]
    public required bool Created { get; init; }
}
