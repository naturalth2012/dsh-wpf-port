using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>One discovered model, mirroring <c>discoveredModelViewSchema</c>.</summary>
public sealed record DiscoveredModelView
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("contextWindow")]
    public int? ContextWindow { get; init; }

    [JsonPropertyName("maxTokens")]
    public int? MaxTokens { get; init; }
}

/// <summary>Value of <c>llm.discoverModels</c>, mirroring <c>llmDiscoverModelsValueSchema</c>.</summary>
public sealed record DiscoverModelsResult
{
    [JsonPropertyName("models")]
    public required DiscoveredModelView[] Models { get; init; }
}
