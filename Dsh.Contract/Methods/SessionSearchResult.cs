using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>
/// Response of <c>session.search</c>, mirroring <c>sessionSearchValueSchema</c>: a capped list
/// of session-id + snippet hits (the host truncates the query and result set; <c>HasMore</c>
/// signals the user should narrow the query).
/// </summary>
public sealed record SessionSearchResult
{
    [JsonPropertyName("items")]
    public required SessionSearchItem[] Items { get; init; }

    [JsonPropertyName("hasMore")]
    public required bool HasMore { get; init; }
}

/// <summary>One <c>session.search</c> hit, mirroring <c>sessionSearchItemSchema</c>.</summary>
public sealed record SessionSearchItem
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("snippet")]
    public required string Snippet { get; init; }
}
