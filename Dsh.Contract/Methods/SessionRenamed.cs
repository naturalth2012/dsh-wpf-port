using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>Response of <c>session.rename</c>, mirroring <c>sessionRenameValueSchema</c>.</summary>
public sealed record SessionRenamed
{
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("seq")]
    public required long Seq { get; init; }
}
