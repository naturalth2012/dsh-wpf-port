using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>Response of <c>host.openPath</c>, mirroring <c>hostOpenPathValueSchema</c>.</summary>
public sealed record OpenPathResult
{
    [JsonPropertyName("opened")]
    public required bool Opened { get; init; }
}
