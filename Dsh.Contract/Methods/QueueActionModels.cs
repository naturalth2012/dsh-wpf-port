using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>
/// One client-requested mutation of a still-pending queue item, mirroring <c>QueueAction</c> in
/// sessions.ts. Discriminated on <c>kind</c>: <c>edit</c> (with content), <c>remove</c>, or
/// <c>steer</c>. Serialized by the custom <see cref="QueueActionConverter"/> below.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(QueueActionEdit), "edit")]
[JsonDerivedType(typeof(QueueActionRemove), "remove")]
[JsonDerivedType(typeof(QueueActionSteer), "steer")]
public abstract record QueueAction;

/// <summary>Edit a pending queue item's content.</summary>
public sealed record QueueActionEdit : QueueAction
{
    [JsonPropertyName("content")]
    public required object[] Content { get; init; }
}

/// <summary>Remove a pending queue item entirely.</summary>
public sealed record QueueActionRemove : QueueAction;

/// <summary>Strict-steer a pending queue item into the current turn.</summary>
public sealed record QueueActionSteer : QueueAction;

/// <summary>Request DTO for <c>session.updateQueue</c>.</summary>
public sealed record UpdateQueueRequest
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    [JsonPropertyName("action")]
    public required QueueAction Action { get; init; }
}

/// <summary>Response DTO for <c>session.updateQueue</c>.</summary>
public sealed record UpdateQueueResult
{
    [JsonPropertyName("accepted")]
    public bool Accepted { get; init; }
}
