using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>
/// Response of <c>host.pickDirectory</c>, mirroring <c>hostPickDirectoryValueSchema</c>:
/// <c>path</c> is null when the user cancelled the picker. The client shows a native folder
/// dialog; this value confirms what the host (loopback, Windows IFileOpenDialog) selected.
/// </summary>
public sealed record PickDirectoryResult
{
    [JsonPropertyName("path")]
    public string? Path { get; init; }
}
