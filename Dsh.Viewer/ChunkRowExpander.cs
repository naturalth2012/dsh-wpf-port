using System.Text.Json;

namespace Dsh.Viewer;

/// <summary>
/// L1 disk-format decode: expands packed storage rows back to their exact original
/// <c>assistant/chunk</c> events. This is a faithful inverse of <c>expandRow</c> in
/// <c>packages/core/session/src/chunk-rows.ts</c>. Storage rows (<c>text-chunks</c> /
/// <c>reasoning-chunks</c> / <c>tool-call-chunks</c>) are a durable-encoding vocabulary, not
/// session events; anything that isn't one of these three passes through verbatim.
/// </summary>
public static class ChunkRowExpander
{
    /// <summary>
    /// Given one decoded JSON log line, return the event(s) to feed to the session fold.
    /// Non-chunk rows are returned as a single-element array (the value itself).
    /// </summary>
    public static JsonElement[] Decode(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String)
        {
            return new[] { value };
        }

        string tag = typeProp.GetString()!;
        if (tag is not ("text-chunks" or "reasoning-chunks" or "tool-call-chunks"))
        {
            return new[] { value };
        }

        return Expand(tag, value);
    }

    private static JsonElement[] Expand(string tag, JsonElement row)
    {
        // seq0 / time0 anchor the first member.
        long seq0 = row.TryGetProperty("seq0", out var s0) && s0.ValueKind == JsonValueKind.Number
            ? s0.GetInt64() : 0;
        long time0 = row.TryGetProperty("time0", out var t0) && t0.ValueKind == JsonValueKind.Number
            ? t0.GetInt64() : 0;

        JsonElement data = row.TryGetProperty("data", out var d) ? d : default;
        JsonElement turn = GetProp(data, "turn");
        JsonElement step = GetProp(data, "step");
        JsonElement index = GetProp(data, "index");
        JsonElement dtArr = GetProp(data, "dt");

        // Members: tool-call-chunks uses args (with id/name run-constant); text/reasoning uses texts.
        JsonElement members = GetProp(data, tag == "tool-call-chunks" ? "args" : "texts");
        string id = GetString(GetProp(data, "id"));
        bool hasName = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("name", out _);
        string name = GetString(GetProp(data, "name"));

        int count = members.ValueKind == JsonValueKind.Array ? members.GetArrayLength() : 0;
        var results = new JsonElement[count];
        long time = time0;
        for (int k = 0; k < count; k++)
        {
            if (k > 0 && dtArr.ValueKind == JsonValueKind.Array && k - 1 < dtArr.GetArrayLength())
            {
                var dk = dtArr[k - 1];
                time += dk.ValueKind == JsonValueKind.Number ? dk.GetInt64() : 0;
            }

            object chunk = tag == "tool-call-chunks"
                ? (hasName
                    ? (object)new { type = "tool-call-delta", index, id, name, argumentsDelta = Str(members[k]) }
                    : new { type = "tool-call-delta", index, id, argumentsDelta = Str(members[k]) })
                : new { type = ChunkType(tag), index, text = Str(members[k]) };

            var ev = new
            {
                type = "assistant/chunk",
                seq = seq0 + k,
                time,
                data = new { turn, step, chunk },
            };
            results[k] = JsonSerializer.SerializeToElement(ev);
        }
        return results;
    }

    private static object? Str(JsonElement el) =>
        el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static string ChunkType(string tag) => tag switch
    {
        "text-chunks" => "text-delta",
        "reasoning-chunks" => "reasoning-delta",
        _ => "tool-call-delta",
    };

    private static JsonElement GetProp(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) ? v : default;

    private static string GetString(JsonElement el) =>
        el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
}
