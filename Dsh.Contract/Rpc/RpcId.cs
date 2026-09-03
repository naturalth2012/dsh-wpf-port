using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Contract.Rpc;

/// <summary>
/// Message correlation id, mirroring <c>RpcId</c> in <c>packages/host/apiproxy/src/api/rpc.ts</c>.
/// Branded string: the initiator mints it on a request; a response echoes the matching
/// request's rpcId and never mints a new one.
///
/// STJ binding: uses an explicit <see cref="RpcIdJsonConverter"/> because STJ's default
/// positional-record binding is unreliable across property-name conventions, and we need
/// deterministic round-trip through a single-string wire shape <c>"rpcId"</c>. Value
/// equality (record default) keys dictionaries by the underlying string.
/// </summary>
[JsonConverter(typeof(RpcIdJsonConverter))]
public record RpcId(string Value)
{
    /// <summary>Wraps a raw id string (compile-time brand, zero runtime cost).</summary>
    public static RpcId Of(string id) => new(id);

    public override string ToString() => Value;
}

/// <summary>
/// Round-trip converter for <see cref="RpcId"/>: on the wire it is a single string
/// (not an object), so it can sit either in an envelope (<c>"rpcId":"abc"</c>) or as the
/// element of a string list, mirroring how the host's TS code treats <c>RpcId</c> as a
/// branded <c>string</c>.
/// </summary>
public sealed class RpcIdJsonConverter : JsonConverter<RpcId>
{
    public override RpcId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? "");

    public override void Write(Utf8JsonWriter writer, RpcId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}