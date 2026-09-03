using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>Response of <c>workspace.archiveSession</c>, mirroring <c>workspaceArchiveSessionValueSchema</c>.</summary>
public sealed record ArchiveSessionResult
{
    [JsonPropertyName("archivedSessionIds")]
    public required string[] ArchivedSessionIds { get; init; }
}
