using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>
/// Feedback rating on a message, mirroring <c>MessageFeedbackRating</c> in message-feedback types.ts:
/// the two values are the literal wire strings "positive" / "negative".
/// </summary>
[JsonConverter(typeof(MessageFeedbackRatingJsonConverter))]
public enum MessageFeedbackRating
{
    [JsonPropertyName("positive")] Positive,
    [JsonPropertyName("negative")] Negative,
}

/// <summary>STJ converter honoring the wire strings of <see cref="MessageFeedbackRating"/>.</summary>
public sealed class MessageFeedbackRatingJsonConverter : JsonConverter<MessageFeedbackRating>
{
    public override MessageFeedbackRating Read(ref System.Text.Json.Utf8JsonReader reader, System.Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType != System.Text.Json.JsonTokenType.String)
        {
            throw new System.Text.Json.JsonException(
                $"Expected a JSON string for MessageFeedbackRating, got '{reader.TokenType}'.");
        }
        var wire = reader.GetString();
        return wire switch
        {
            "positive" => MessageFeedbackRating.Positive,
            "negative" => MessageFeedbackRating.Negative,
            _ => throw new System.Text.Json.JsonException($"Unknown MessageFeedbackRating wire value: '{wire}'."),
        };
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, MessageFeedbackRating value, System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            MessageFeedbackRating.Positive => "positive",
            _ => "negative",
        });
    }
}

/// <summary>One stored feedback item, mirroring <c>MessageFeedbackItem</c>.</summary>
public sealed record MessageFeedbackItem
{
    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }

    [JsonPropertyName("rating")]
    public required MessageFeedbackRating Rating { get; init; }

    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }

    [JsonPropertyName("version")]
    public required int Version { get; init; }

    [JsonPropertyName("createdAt")]
    public required long CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public required long UpdatedAt { get; init; }
}

/// <summary>Request of <c>messageFeedback.list</c>.</summary>
public sealed record MessageFeedbackListRequest
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }
}

/// <summary>Value of <c>messageFeedback.list</c>, mirroring <c>MessageFeedbackListValue</c>.</summary>
public sealed record MessageFeedbackListValue
{
    [JsonPropertyName("items")]
    public required MessageFeedbackItem[] Items { get; init; }
}

/// <summary>Request of <c>messageFeedback.put</c>.</summary>
public sealed record MessageFeedbackPutRequest
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }

    [JsonPropertyName("rating")]
    public required MessageFeedbackRating Rating { get; init; }

    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }

    /// <summary>Expected current version for optimistic concurrency; null means create-if-absent.</summary>
    [JsonPropertyName("ifVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? IfVersion { get; init; }
}

/// <summary>Request of <c>messageFeedback.delete</c>.</summary>
public sealed record MessageFeedbackDeleteRequest
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }

    [JsonPropertyName("ifVersion")]
    public required int IfVersion { get; init; }
}

/// <summary>Value of <c>messageFeedback.delete</c> (an absent marker).</summary>
public sealed record MessageFeedbackDeleteValue
{
    [JsonPropertyName("absent")]
    public required bool Absent { get; init; }
}
