using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>Response of <c>session.prompt</c>, mirroring <c>sessionPromptValueSchema</c>.
/// The <c>command</c> slot appears only when the prompt dispatched a slash command.</summary>
public sealed record SessionPromptResult
{
    [JsonPropertyName("accepted")]
    public required bool Accepted { get; init; }

    [JsonPropertyName("command")]
    public PromptCommand? Command { get; init; }

    public sealed record PromptCommand
    {
        [JsonPropertyName("kind")]
        public required string Kind { get; init; }

        [JsonPropertyName("text")]
        public string? Text { get; init; }
    }
}
