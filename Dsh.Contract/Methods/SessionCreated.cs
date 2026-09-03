using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>Response of <c>session.create</c>, mirroring <c>sessionCreateValueSchema</c>.</summary>
public sealed record SessionCreated
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("agentPreset")]
    public string? AgentPreset { get; init; }
}
