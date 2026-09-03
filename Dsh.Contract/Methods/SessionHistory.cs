using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>One <c>session.history</c> item: the session event plus optional tool view.</summary>
public sealed record HistoryEntry
{
    [JsonPropertyName("event")]
    public required JsonElement Event { get; init; }

    [JsonPropertyName("view")]
    public JsonElement? View { get; init; }
}

/// <summary>Value of <c>session.history</c>, mirroring <c>sessionHistoryValueSchema</c>.</summary>
public sealed record SessionHistoryPage
{
    [JsonPropertyName("events")]
    public required HistoryEntry[] Events { get; init; }

    [JsonPropertyName("hasMore")]
    public required bool HasMore { get; init; }

    [JsonPropertyName("projections")]
    public JsonElement? Projections { get; init; }
}
