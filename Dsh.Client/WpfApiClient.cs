using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dsh.Contract.Rpc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dsh.Client;

/// <summary>
/// Host gateway client, mirroring <c>packages/client/connection</c>: unary calls over
/// <c>POST /api/&lt;method&gt;</c>, client-responses over <c>POST /api/respond</c>, and two
/// downstream WebSocket streams (mux + host). The client is configured against the host's
/// loopback gateway and enforces the JSON media type the loopback trust gate expects.
/// </summary>
public sealed class WpfApiClient : IRespondClient, IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly JsonEnvelopeCodec _codec;
    private readonly string _baseUrl;
    private readonly TimeSpan _unaryTimeout;
    private readonly ILogger _log;

    public ConnectionGeneration Generation { get; } = new();

    public WpfApiClient(
        string baseUrl,
        TimeSpan? unaryTimeout = null,
        HttpMessageHandler? handler = null,
        ILogger? logger = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _unaryTimeout = unaryTimeout ?? TimeSpan.FromSeconds(30);
        _codec = new JsonEnvelopeCodec();
        _log = logger ?? NullLogger.Instance;

        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = _unaryTimeout;
        // The loopback trust gate rejects non-JSON media types on privileged APIs.
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Unary call: POST /api/&lt;method&gt; with a minted rpcId; returns the success value
    /// or throws a typed <see cref="RpcException"/> carrying the business error. Network/HTTP
    /// failures surface as <see cref="HttpRequestException"/> (the reconnect layer owns retry).
    /// </summary>
    public async Task<T> Call<T>(string method, object? payload, CancellationToken ct = default)
    {
        var rpcId = RpcId.Of(Guid.NewGuid().ToString("N"));

        // Host schemas are always `z.object({...})` (never `z.unknown()`): a literal
        // JSON null fails Zod parsing ("invalid payload for <method>"). Substitute the
        // empty object literal so methods with no payload (e.g. host.describe,
        // session.list, session.history) succeed without each call site having to
        // remember to pass `new { }`.
        object effectivePayload = payload ?? new { };

        var request = new ClientRequest { RpcId = rpcId, Method = method, Payload = effectivePayload };
        string body = _codec.SerializeRequest(request);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _log.LogDebug("RPC → {Method} (rpcId {RpcId})", method, rpcId.Value);

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"{_baseUrl}/api/{method}", content, ct)
            .ConfigureAwait(false);

        // The host returns 200 for all business outcomes; non-200 is a wire failure.
        response.EnsureSuccessStatusCode();

        byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var serverResponse = _codec.DeserializeServerResponse(bytes);
        sw.Stop();

        // serverResponse.Result is a JsonElement (STJ materializes `object` slots as JSON).
        var raw = (JsonElement)(serverResponse.Result ?? throw new JsonException("Empty result slot."));

        // Discriminate on ok: { ok:true, value:T } vs { ok:false, error:RpcError }
        if (!raw.TryGetProperty("ok", out var okProp) || !okProp.GetBoolean())
        {
            // Error slot shape: { ok:false, error:{ code, message, details } }
            if (!raw.TryGetProperty("error", out var errorProp))
            {
                throw new JsonException($"Error slot for '{method}' had no `error` field.");
            }
            var error = errorProp.Deserialize<RpcError>(JsonEnvelopeCodec.Options);
            _log.LogWarning("RPC {Method} failed ({Code}) in {ElapsedMs}ms",
                method, error is null ? "?" : error.Code.ToString(), sw.ElapsedMilliseconds);
            if (error is not null) throw new RpcException(error);
            throw new JsonException($"`error` for '{method}' was not a valid RpcError.");
        }

        // Success slot: extract `value` and deserialize to T.
        if (!raw.TryGetProperty("value", out var valueProp))
        {
            throw new JsonException($"Success slot for '{method}' had no `value`.");
        }
        var value = valueProp.Deserialize<T>(JsonEnvelopeCodec.Options)
            ?? throw new JsonException($"Null `value` for '{method}'.");
        // Record shape hints useful for diagnosing empty/incomplete responses (e.g. a host
        // that has no LLM adapters registered returns groups=[] from session.models).
        _log.LogDebug("RPC ← {Method} ok in {ElapsedMs}ms", method, sw.ElapsedMilliseconds);
        LogResponseShape(method, valueProp);
        return value;
    }

    /// <summary>
    /// Client-response to an answerable server-request (approval/question): POST /api/respond
    /// with the echoed rpcId. Returns the carrier receipt; late/duplicate answers are not-pending.
    /// </summary>
    public async Task<RpcReceipt> Respond(RpcId rpcId, object? result, CancellationToken ct = default)
    {
        var response = new ClientResponse { RpcId = rpcId, Result = result };
        string body = _codec.SerializeResponse(response);

        _log.LogDebug("respond → {RpcId}", rpcId.Value);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var httpResponse = await _http.PostAsync($"{_baseUrl}/api/respond", content, ct)
            .ConfigureAwait(false);

        httpResponse.EnsureSuccessStatusCode();

        byte[] bytes = await httpResponse.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var receipt = System.Text.Json.JsonSerializer.Deserialize<RpcReceipt>(bytes, JsonEnvelopeCodec.Options)
            ?? throw new JsonException("Empty respond receipt.");
        _log.LogDebug("respond ← {RpcId} accepted={Accepted}", rpcId.Value, receipt.Accepted);
        return receipt;
    }

    /// <summary>
    /// Logs a compact shape hint of an RPC response value so the host's empty/incomplete
    /// cases (e.g. session.models returning groups=[] when no LLM adapter is registered)
    /// are visible in dsh-client.log without dumping the whole payload.
    /// </summary>
    private void LogResponseShape(string method, System.Text.Json.JsonElement valueProp)
    {
        if (valueProp.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        if (method == "session.models")
        {
            var groups = valueProp.TryGetProperty("groups", out var g) && g.ValueKind == System.Text.Json.JsonValueKind.Array
                ? g.GetArrayLength() : 0;
            var failures = valueProp.TryGetProperty("failures", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.Array
                ? f.GetArrayLength() : 0;
            var current = valueProp.TryGetProperty("current", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Object
                ? $"{(c.TryGetProperty("provider", out var cp) ? cp.GetString() : "?")}/{(c.TryGetProperty("model", out var cm) ? cm.GetString() : "?")}"
                : "(none)";
            var routable = valueProp.TryGetProperty("routable", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.True;
            _log.LogDebug("session.models shape: groups={Groups} failures={Failures} routable={Routable} current={Current}",
                groups, failures, routable, current);
        }
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
