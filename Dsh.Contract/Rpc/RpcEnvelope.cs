using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Contract.Rpc;

/// <summary>
/// The four wire full forms of the RPC message model, mirroring rpc.ts:
/// a discriminated union keyed by the <c>type</c> literal.
///
/// STJ binding: a <see cref="RpcMessageTypeJsonConverter"/> honors each member's
/// <c>[JsonPropertyName]</c>; the default enum serializer would write
/// <c>"ClientRequest"</c> (enum name) instead of the wire <c>"client-request"</c>,
/// causing host schema rejection ("invalid client-request message").
/// </summary>
[JsonConverter(typeof(RpcMessageTypeJsonConverter))]
public enum RpcMessageType
{
    [JsonPropertyName("client-request")] ClientRequest,
    [JsonPropertyName("server-response")] ServerResponse,
    [JsonPropertyName("server-request")] ServerRequest,
    [JsonPropertyName("client-response")] ClientResponse,
}

/// <summary>
/// STJ converter for <see cref="RpcMessageType"/> that honors each member's
/// <c>[JsonPropertyName]</c>, mirroring how the host wire keys enum members.
/// </summary>
public sealed class RpcMessageTypeJsonConverter : JsonConverter<RpcMessageType>
{
    // One index build feeds both directions (see RpcErrorCodeJsonConverter for rationale:
    // cached reverse map instead of per-Write reflection, symmetric with Read).
    private static readonly Dictionary<string, RpcMessageType> _byWire = new(StringComparer.Ordinal);
    private static readonly Dictionary<RpcMessageType, string> _toWire = new();

    static RpcMessageTypeJsonConverter()
    {
        foreach (var field in typeof(RpcMessageType).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var attr = field.GetCustomAttribute<JsonPropertyNameAttribute>()
                ?? throw new InvalidOperationException($"RpcMessageType.{field.Name} missing [JsonPropertyName].");
            var value = (RpcMessageType)field.GetValue(null)!;
            _byWire[attr.Name] = value;
            _toWire[value] = attr.Name;
        }
    }

    public override RpcMessageType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var wire = reader.GetString() ?? throw new JsonException("RpcMessageType expected non-null string.");
        if (_byWire.TryGetValue(wire, out var value)) return value;
        throw new JsonException($"Unknown RpcMessageType wire value: '{wire}'.");
    }

    public override void Write(Utf8JsonWriter writer, RpcMessageType value, JsonSerializerOptions options)
    {
        if (!_toWire.TryGetValue(value, out var name))
        {
            throw new InvalidOperationException($"RpcMessageType.{value} has no [JsonPropertyName] mapping.");
        }
        writer.WriteStringValue(name);
    }
}

/// <summary>
/// Call initiated by the client (wire carrier: <c>POST /api/&lt;method&gt;</c> body).
/// <c>Type</c> is a get-only discriminator fixed by the concrete subtype (a true record
/// union); STJ still includes it on the wire as a constant string.
/// </summary>
public sealed record ClientRequest
{
    public RpcMessageType Type => RpcMessageType.ClientRequest;
    public required RpcId RpcId { get; init; }
    public required string Method { get; init; }
    public object? Payload { get; init; }
}

/// <summary>
/// Response to a <see cref="ClientRequest"/> (the HTTP response body of that POST); rpcId echoed.
/// </summary>
public sealed record ServerResponse
{
    public RpcMessageType Type => RpcMessageType.ServerResponse;
    public required RpcId RpcId { get; init; }
    public required object? Result { get; init; }
}

/// <summary>
/// Message initiated by the server (downstream stream frame). Answerable interactions
/// (approval/question requested — stable rpcId) and pure pushes share this shape.
/// </summary>
public sealed record ServerRequest
{
    public RpcMessageType Type => RpcMessageType.ServerRequest;
    public required RpcId RpcId { get; init; }
    public required string Method { get; init; }
    public object? Payload { get; init; }
}

/// <summary>
/// Response to a <see cref="ServerRequest"/> (wire carrier: <c>POST /api/respond</c>); rpcId echoed.
/// </summary>
public sealed record ClientResponse
{
    public RpcMessageType Type => RpcMessageType.ClientResponse;
    public required RpcId RpcId { get; init; }
    public required object? Result { get; init; }
}

/// <summary>
/// Carrier receipt for a client-response, mirroring <c>RpcReceipt</c> in rpc.ts:
/// the HTTP response body of the POST carrying a client-response. Late/duplicate
/// responses yield not-pending.
/// </summary>
public sealed record RpcReceipt
{
    public required bool Accepted { get; init; }

    /// <summary>Only present when <see cref="Accepted"/> is false.</summary>
    public string? Reason { get; init; }

    public static RpcReceipt Ok() => new() { Accepted = true };

    public static RpcReceipt NotPending() => new() { Accepted = false, Reason = "not-pending" };

    public static RpcReceipt BadResponse() => new() { Accepted = false, Reason = "bad-response" };
}
