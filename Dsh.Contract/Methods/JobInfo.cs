using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>
/// One background job, mirroring <c>PublicJobSnapshot</c> in tool-jobs: task state safe for
/// the client surface (ownership fields omitted). Status is one of the job lifecycle states
/// (queued/running/stopping/completed/...).
/// </summary>
public sealed record JobInfo
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    [JsonPropertyName("startedAt")]
    public long StartedAt { get; init; }

    [JsonPropertyName("finishedAt")]
    public long? FinishedAt { get; init; }
}
