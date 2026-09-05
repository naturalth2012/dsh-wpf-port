using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Contract.Methods;

/// <summary>
/// One queued/steering inbox item, mirroring <c>QueuedInboxItem</c> in events.ts.
/// <c>Placement</c>: queued (QueueDock), steering (conversation tail), context (invisible until claimed).
/// </summary>
public sealed record QueuedInboxItem
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("placement")]
    public required string Placement { get; init; }

    [JsonPropertyName("message")]
    public JsonElement? Message { get; init; }

    /// <summary>Short human text for a queue row (from message.content text part if present).
    /// Computed view helper, not a wire field — [JsonIgnore] keeps it out of request/response
    /// serialization (a stray get-only property would otherwise leak onto the wire as noise).</summary>
    [JsonIgnore]
    public string TextPreview
    {
        get
        {
            if (Message is not JsonElement m || m.ValueKind != JsonValueKind.Object) return "";
            if (m.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                        block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        string s = text.GetString() ?? "";
                        return s.Length > 60 ? s[..60] + "…" : s;
                    }
                }
            }
            return "";
        }
    }
}
