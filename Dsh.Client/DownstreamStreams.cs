using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Dsh.Contract.Frames;
using Dsh.Contract.Rpc;
using Microsoft.Extensions.Logging;

namespace Dsh.Client;

/// <summary>
/// WebSocket downstream stream reader, mirroring
/// <c>packages/client/connection/src/websocket-downlink.ts</c>: the host's two
/// server-to-client event streams are WebSocket downlinks (<c>/api/events.mux</c> and
/// <c>/api/events.host</c>). Each WS text message is a <c>ServerRequest</c> envelope
/// (<c>JSON.stringify({ type:'server-request', rpcId, method, payload })</c>); the client
/// must never send upstream — the host closes 1008 on a client message.
/// </summary>
public static class DownstreamStreams
{
    /// <summary>Read the mux downlink as mux frames with their rpcId.</summary>
    public static async IAsyncEnumerable<(RpcId RpcId, MuxFrame Frame)> ReadMux(
        string baseUrl,
        ILogger log,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var (rpcId, frame) in ReadDownlink<MuxFrame>(baseUrl, "/api/events.mux", log, ct))
        {
            yield return (rpcId, frame);
        }
    }

    /// <summary>Read the host downlink as host frames with their rpcId.</summary>
    public static async IAsyncEnumerable<(RpcId RpcId, HostFrame Frame)> ReadHost(
        string baseUrl,
        ILogger log,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var (rpcId, frame) in ReadDownlink<HostFrame>(baseUrl, "/api/events.host", log, ct))
        {
            yield return (rpcId, frame);
        }
    }

    private static async IAsyncEnumerable<(RpcId RpcId, TFrame Frame)> ReadDownlink<TFrame>(
        string baseUrl,
        string path,
        ILogger log,
        [EnumeratorCancellation] CancellationToken ct)
        where TFrame : class
    {
        // Convert http:// to ws:// for the upgrade.
        var wsUrl = new Uri(baseUrl.Replace("http://", "ws://").Replace("https://", "wss://") + path);

        using var socket = new ClientWebSocket();
        try
        {
            log.LogInformation("[Stream] WS connecting {Url}", wsUrl);
            await socket.ConnectAsync(wsUrl, ct).ConfigureAwait(false);
            log.LogInformation("[Stream] WS connected {Url} (state={State})", wsUrl, socket.State);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A cancelled connect (shutdown) propagates cleanly without a warning; every other
            // failure is logged once here and rethrown so the reconnect loop can react.
            log.LogWarning("[Stream] WS connect failed {Url}: {Ex}", wsUrl, ex.Message);
            throw;
        }

        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Host closed (e.g. 1008 'downlink only' on a protocol violation).
                    yield break;
                }
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            string text = Encoding.UTF8.GetString(message.ToArray());
            if (TryParseFrame<TFrame>(text, log, out var rpcId, out var frame))
            {
                BackfillRpcId(frame, rpcId);
                yield return (rpcId, frame);
            }
        }
    }

    /// <summary>
    /// Backfill the envelope's rpcId onto the parsed frame. The host carries rpcId only on the
    /// enclosing server-request envelope, never inside the frame payload, so BOTH frame
    /// hierarchies (<see cref="MuxFrame"/> and <see cref="HostFrame"/>) model it as an optional
    /// settable property that this reader fills in after parsing.
    ///
    /// Regression note (2026-09-05): the host hierarchy previously modeled RpcId as `required`,
    /// so every real host-frame payload (which never embeds rpcId) failed STJ validation, was
    /// swallowed by the corrupt-frame catch, and the entire host stream ran deaf — while all
    /// wire tests stayed green because their fixtures wrongly embedded rpcId in the payload.
    /// </summary>
    internal static void BackfillRpcId(object frame, RpcId rpcId)
    {
        switch (frame)
        {
            case MuxFrame { RpcId: null } mux:
                mux.RpcId = rpcId;
                break;
            case HostFrame { RpcId: null } host:
                host.RpcId = rpcId;
                break;
        }
    }

    /// <summary>
    /// Parse one WS text message as a server-request envelope
    /// (<c>{ type:'server-request', rpcId, method, payload }</c>) and deserialize the payload
    /// slot into <typeparamref name="TFrame"/>. Malformed or foreign messages return false —
    /// one bad frame must not kill the stream — but every drop is logged with a reason and a
    /// process-lifetime counter, so a contract drift can never deafen a stream silently again.
    /// </summary>
    internal static bool TryParseFrame<TFrame>(
        string text,
        ILogger log,
        out RpcId rpcId,
        out TFrame frame)
        where TFrame : class
    {
        rpcId = default!;
        frame = null!;

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            // Envelope: { type:"server-request", rpcId, method, payload }. Missing slots go
            // through the logged drop path (TryGetProperty) instead of throwing
            // KeyNotFoundException, which previously escaped the JsonException-only catch and
            // could kill the whole receive loop.
            if (!root.TryGetProperty("type", out var typeEl) ||
                typeEl.GetString() != "server-request")
            {
                DropFrame(log, text, "not a server-request envelope");
                return false;
            }

            if (!root.TryGetProperty("rpcId", out var rpcIdEl) ||
                rpcIdEl.GetString() is not { Length: > 0 } rpcIdText)
            {
                DropFrame(log, text, "envelope missing rpcId");
                return false;
            }

            if (!root.TryGetProperty("payload", out var payload))
            {
                DropFrame(log, text, "envelope missing payload");
                return false;
            }

            rpcId = RpcId.Of(rpcIdText);

            var parsed = JsonSerializer.Deserialize<TFrame>(payload.GetRawText(), JsonEnvelopeCodec.Options);
            if (parsed is null)
            {
                DropFrame(log, text, "payload deserialized to null");
                return false;
            }
            frame = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            DropFrame(log, text, $"JSON parse failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Cumulative count of frames dropped as unparseable (diagnostic counter).</summary>
    private static long _droppedFrames;

    private static void DropFrame(ILogger log, string text, string reason)
    {
        long count = Interlocked.Increment(ref _droppedFrames);
        string head = text.Length <= 200 ? text : text[..200];
        log.LogWarning("[Stream] dropped frame #{Count} ({Reason}): {Head}", count, reason, head);
    }
}
