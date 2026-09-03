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
            if (TryParseFrame<TFrame>(text, out var rpcId, out var frame))
            {
                // The host carries rpcId only on the envelope, not inside the frame payload.
                // Backfill it so answerable frames (approval/question) can echo it on response.
                if (frame is Dsh.Contract.Frames.MuxFrame mf && mf.RpcId is null)
                {
                    mf.RpcId = rpcId;
                }
                yield return (rpcId, frame);
            }
        }
    }

    private static bool TryParseFrame<TFrame>(
        string text,
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

            // Envelope: { type:"server-request", rpcId, method, payload }
            string type = root.GetProperty("type").GetString() ?? "";
            if (type != "server-request") return false;

            rpcId = RpcId.Of(root.GetProperty("rpcId").GetString() ?? "");
            var payload = root.GetProperty("payload");

            var parsed = JsonSerializer.Deserialize<TFrame>(payload.GetRawText(), JsonEnvelopeCodec.Options);
            if (parsed is null) return false;
            frame = parsed;
            return true;
        }
        catch (JsonException)
        {
            // One corrupt frame must not kill the stream.
            return false;
        }
    }
}
