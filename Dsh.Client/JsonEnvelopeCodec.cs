using System.Text.Json;
using Dsh.Contract.Rpc;

namespace Dsh.Client;

/// <summary>
/// Encodes/decodes the four wire full forms (<see cref="ClientRequest"/>, <see cref="ServerResponse"/>,
/// <see cref="ServerRequest"/>, <see cref="ClientResponse"/>) with <c>System.Text.Json</c>.
/// The wire contract is in rpc.ts; this codec is the single place the client maps wire JSON
/// to the strong contract types and translates host error codes to <see cref="RpcErrorCode"/>.
/// </summary>
public sealed class JsonEnvelopeCodec
{
    /// <summary>Serializer options shared by request/response and frame deserialization.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Serialize a client-request (the body of <c>POST /api/&lt;method&gt;</c>).</summary>
    public string SerializeRequest(ClientRequest request) =>
        JsonSerializer.Serialize(request, Options);

    /// <summary>Serialize a client-response (the body of <c>POST /api/respond</c>).</summary>
    public string SerializeResponse(ClientResponse response) =>
        JsonSerializer.Serialize(response, Options);

    /// <summary>
    /// Deserialize the HTTP response body of a unary call into a <see cref="ServerResponse"/>.
    /// The response always carries the echoed rpcId and a result slot; business errors are
    /// represented as <c>{ ok:false, error:{code,message,details} }</c>, never as HTTP errors.
    /// </summary>
    public ServerResponse DeserializeServerResponse(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize<ServerResponse>(utf8Json, Options)
        ?? throw new JsonException("Empty server-response body.");
}
