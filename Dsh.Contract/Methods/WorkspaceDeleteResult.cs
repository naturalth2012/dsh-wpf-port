using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>
/// Response of <c>workspace.delete</c>, mirroring <c>workspaceDeleteValueSchema</c>: the
/// literal <c>deleted: true</c> marker. The host removes the workspace registry entry and
/// the surfaced grouping (the underlying directory and session logs are untouched).
/// </summary>
public sealed record WorkspaceDeleteResult
{
    [JsonPropertyName("deleted")]
    public required bool Deleted { get; init; }
}
