using System.IO;
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
        // seq0 / time0 anchor the first member (absent fields default to 0, matching upstream).
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
            results[k] = SerializeChunk(tag, seq0 + k, time, turn, step, index, id, name, hasName, members[k]);
        }
        return results;
    }

    /// <summary>
    /// Serialize one expanded <c>assistant/chunk</c> event, writing only the optional fields that
    /// are actually present. A <c>default</c> <see cref="JsonElement"/> (absent property) cannot be
    /// serialized, so the previous anonymous-object approach threw <c>InvalidOperationException</c>
    /// on chunk rows that omit optional members — this builder is resilient to that.
    /// </summary>
    private static JsonElement SerializeChunk(string tag, long seq, long time, JsonElement turn,
        JsonElement step, JsonElement index, string id, string name, bool hasName, JsonElement member)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();                 // assistant/chunk
            w.WriteString("type", "assistant/chunk");
            w.WriteNumber("seq", seq);
            w.WriteNumber("time", time);
            w.WritePropertyName("data");
            w.WriteStartObject();                 // data
            WriteIfPresent(w, "turn", turn);
            WriteIfPresent(w, "step", step);
            w.WritePropertyName("chunk");
            w.WriteStartObject();                 // chunk
            w.WriteString("type", ChunkType(tag));
            WriteIfPresent(w, "index", index);
            if (tag == "tool-call-chunks")
            {
                if (id.Length != 0) w.WriteString("id", id);
                if (hasName && name.Length != 0) w.WriteString("name", name);
                WriteStringOrNull(w, "argumentsDelta", member);
            }
            else
            {
                WriteStringOrNull(w, "text", member);
            }
            w.WriteEndObject();                   // /chunk
            w.WriteEndObject();                   // /data
            w.WriteEndObject();                   // /assistant/chunk
        }
        return JsonSerializer.Deserialize<JsonElement>(ms.ToArray());
    }

    /// <summary>Write a property only when its source <see cref="JsonElement"/> is non-default.</summary>
    private static void WriteIfPresent(Utf8JsonWriter w, string name, JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Undefined) return;
        w.WritePropertyName(name);
        el.WriteTo(w);
    }

    private static void WriteStringOrNull(Utf8JsonWriter w, string name, JsonElement el)
    {
        w.WritePropertyName(name);
        if (el.ValueKind == JsonValueKind.String) w.WriteStringValue(el.GetString());
        else w.WriteNullValue();
    }

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
