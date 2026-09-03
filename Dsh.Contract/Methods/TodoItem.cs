using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>One todo entry, mirroring <c>TodoItem</c> in core/session/types.ts.</summary>
public sealed record TodoItem
{
    [JsonPropertyName("content")]
    public required string Content { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }
}
