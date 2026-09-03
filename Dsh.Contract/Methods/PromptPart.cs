using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>
/// One content part of <c>session.prompt</c> <c>content</c>, mirroring
/// <c>promptContentPartSchema</c>: a discriminated union keyed by <c>type</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextPart), "text")]
[JsonDerivedType(typeof(ImagePart), "image")]
public abstract record PromptPart;

/// <summary>Plain-text content part.</summary>
public sealed record TextPart : PromptPart
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

/// <summary>Inline image content part (base64 data).</summary>
public sealed record ImagePart : PromptPart
{
    [JsonPropertyName("mediaType")]
    public required string MediaType { get; init; }

    [JsonPropertyName("data")]
    public required string Data { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

/// <summary>Helper factory for common prompt parts.</summary>
public static class PromptParts
{
    public static PromptPart Text(string text) => new TextPart { Text = text };

    public static PromptPart Image(string mediaType, string base64Data, string? name = null) =>
        new ImagePart { MediaType = mediaType, Data = base64Data, Name = name };
}
