using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>A selected provider/model pair (mirrors <c>modelSelectionSchema</c>).</summary>
public sealed record ModelSelection
{
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("reasoningEffort")]
    public string? ReasoningEffort { get; init; }
}

/// <summary>A catalog model within a provider group (mirrors <c>modelCatalogModelSchema</c>).</summary>
public sealed record ModelCatalogModel
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

/// <summary>One successfully loaded provider group (mirrors <c>modelProviderGroupSchema</c>).</summary>
public sealed record ModelProviderGroup
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("models")]
    public ModelCatalogModel[]? Models { get; init; }
}

/// <summary>One provider-local catalog failure (mirrors <c>modelCatalogFailureSchema</c>).</summary>
public sealed record ModelCatalogFailure
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

/// <summary>Value of <c>session.models</c> (mirrors <c>sessionModelsValueSchema</c>).</summary>
public sealed record ModelsResult
{
    [JsonPropertyName("current")]
    public ModelSelection? Current { get; init; }

    [JsonPropertyName("routable")]
    public required bool Routable { get; init; }

    [JsonPropertyName("groups")]
    public ModelProviderGroup[]? Groups { get; init; }

    [JsonPropertyName("failures")]
    public ModelCatalogFailure[]? Failures { get; init; }
}

/// <summary>Value of <c>session.selectModel</c> (mirrors <c>sessionSelectModelValueSchema</c>).</summary>
public sealed record SelectModelResult
{
    [JsonPropertyName("selected")]
    public required ModelSelection Selected { get; init; }
}
