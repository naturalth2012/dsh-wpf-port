using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>
/// Response of <c>session.fork</c>, mirroring <c>sessionForkValueSchema</c>: the new child
/// session id created by cutting at <c>atSeq</c> (or the latest completed-turn boundary).
/// </summary>
public sealed record SessionForkResult
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }
}
