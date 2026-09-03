using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>
/// Response of <c>agentPreset.list</c>, mirroring <c>agentPresetListValueSchema</c>. Each
/// preset <c>id</c> (or <c>name</c> when present) doubles as a slash-command candidate
/// (<c>/&lt;name&gt;</c>) for the C12 command catalog.
/// </summary>
public sealed record AgentPresetListResult
{
    [JsonPropertyName("presets")]
    public required AgentPresetEntry[] Presets { get; init; }

    [JsonPropertyName("authorable")]
    public required bool Authorable { get; init; }

    [JsonPropertyName("hasDocument")]
    public required bool HasDocument { get; init; }
}

/// <summary>One preset row of <c>agentPreset.list</c>, mirroring <c>agentPresetEntrySchema</c>.</summary>
public sealed record AgentPresetEntry
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("trust")]
    public required string Trust { get; init; }

    [JsonPropertyName("isDefault")]
    public required bool IsDefault { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}
