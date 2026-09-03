using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>One selectable answer option, mirroring <c>AskUserQuestionOption</c>.</summary>
public sealed record QuestionOption
{
    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

/// <summary>Presentation intent; a UI may present it specially but the answer is identical.</summary>
public sealed record QuestionIntent
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("approve")]
    public string? Approve { get; init; }
}

/// <summary>One question in a request, mirroring <c>AskUserQuestionItem</c>.</summary>
public sealed record QuestionItem
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("question")]
    public required string Question { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    [JsonPropertyName("header")]
    public string? Header { get; init; }

    [JsonPropertyName("options")]
    public QuestionOption[]? Options { get; init; }

    [JsonPropertyName("multiSelect")]
    public bool MultiSelect { get; init; }

    [JsonPropertyName("intent")]
    public QuestionIntent? Intent { get; init; }
}
