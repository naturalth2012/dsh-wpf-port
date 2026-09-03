using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>
/// Discriminated subagent catalog row, mirroring <c>SubagentListEntry</c>: the wire
/// discriminates on <c>kind</c> ("child" | "diagnostic").
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SubagentChild), "child")]
[JsonDerivedType(typeof(SubagentDiagnostic), "diagnostic")]
public abstract record SubagentEntry
{
    public required string Id { get; init; }

    public bool IsChild => this is SubagentChild;
}

/// <summary>A healthy child entry (one-shot or continuable).</summary>
public sealed record SubagentChild : SubagentEntry
{
    [JsonPropertyName("activity")]
    public required string Activity { get; init; }

    [JsonPropertyName("hasChildren")]
    public bool HasChildren { get; init; }

    [JsonPropertyName("mode")]
    public required string Mode { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }
}

/// <summary>A corrupt/unsupported/unavailable child.</summary>
public sealed record SubagentDiagnostic : SubagentEntry
{
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

/// <summary>Complete direct-child catalog plus parent availability hint.</summary>
public sealed record SubagentCatalog
{
    [JsonPropertyName("entries")]
    public required SubagentEntry[] Entries { get; init; }

    [JsonPropertyName("parentAvailable")]
    public bool ParentAvailable { get; init; }
}

/// <summary>Durable parent/child address selecting subagent transport.</summary>
public sealed record SubagentAddress
{
    [JsonPropertyName("parentSessionId")]
    public required string ParentSessionId { get; init; }

    [JsonPropertyName("childSessionId")]
    public required string ChildSessionId { get; init; }

    [JsonPropertyName("mode")]
    public required string Mode { get; init; }
}

/// <summary>One transcript page for a child, mirroring the <c>subagent.history</c> result.</summary>
public sealed record SubagentHistory
{
    [JsonPropertyName("events")]
    public object?[] Events { get; init; } = [];

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }
}

/// <summary>Inbox identity once a continuation accepts a message.</summary>
public sealed record SubagentPromptReceipt
{
    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }
}

/// <summary>Uniform acknowledgement that one interrupt request was admitted.</summary>
public sealed record SubagentInterruptReceipt
{
    [JsonPropertyName("accepted")]
    public bool Accepted { get; init; }
}
