using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>
/// One session list entry, mirroring <c>SessionSummary</c> in sessions.ts. MVP scope:
/// fields required by the session sidebar (id, title, blank bit, cwd). Unknown wire
/// keys are ignored (STJ default), so the projection sub-object can grow without
/// breaking the client.
/// </summary>
public sealed record SessionSummary
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("updatedAt")]
    public required long UpdatedAt { get; init; }

    [JsonPropertyName("running")]
    public required bool Running { get; init; }

    [JsonPropertyName("blank")]
    public required bool Blank { get; init; }

    [JsonPropertyName("parentSessionId")]
    public string? ParentSessionId { get; init; }

    [JsonPropertyName("origin")]
    public string? Origin { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("agentPreset")]
    public string? AgentPreset { get; init; }

    [JsonPropertyName("projections")]
    public JsonElement? Projections { get; init; }
}

/// <summary>
/// The wire shape of a successful <c>session.list</c> response, mirroring the
/// <c>sessionListValueSchema</c> value slot: <c>{ items: SessionSummary[] }</c>.
/// </summary>
public sealed record SessionListResponse
{
    [JsonPropertyName("items")]
    public required SessionSummary[] Items { get; init; }
}