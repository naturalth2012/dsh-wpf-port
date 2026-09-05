using Dsh.Client;
using Dsh.Contract.Frames;
using Dsh.Contract.Rpc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dsh.Client.Tests;

/// <summary>
/// Transport-layer regression gate for the server-request envelope parser and the rpcId
/// backfill. These tests replay the REAL wire shape — payload slots that do NOT embed rpcId
/// (the host carries rpcId only on the envelope). The wire fixtures in Dsh.Contract.Tests
/// previously embedded "rpcId" inside every payload, a shape the host never sends, which is
/// exactly what let the HostFrame `required RpcId` contract drift pass every test while the
/// production host stream ran deaf: every real payload failed STJ validation and the
/// corrupt-frame catch silently dropped it.
/// </summary>
public class DownstreamStreamsTests
{
    private static readonly ILogger Log = NullLogger.Instance;

    // ---------------------------------------------------------------------------------------
    // Envelope parsing
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Parses_server_request_envelope_into_mux_frame()
    {
        const string text =
            "{\"type\":\"server-request\",\"rpcId\":\"r1\",\"method\":\"session/event\"," +
            "\"payload\":{\"type\":\"session/subscribed\",\"sessionId\":\"s1\",\"lastSeq\":7}}";

        bool ok = DownstreamStreams.TryParseFrame<MuxFrame>(text, Log, out var rpcId, out var frame);

        Assert.True(ok);
        Assert.Equal(RpcId.Of("r1"), rpcId);
        var subscribed = Assert.IsType<SessionSubscribedFrame>(frame);
        Assert.Equal("s1", subscribed.SessionId);
        Assert.Equal(7L, subscribed.LastSeq);
    }

    /// <summary>
    /// THE P0 regression (2026-09-05): the host never embeds rpcId in the payload. With the
    /// old `required RpcId` contract on HostFrame, STJ threw JsonException on this exact shape,
    /// the corrupt-frame catch swallowed it, and the entire host stream was silently deaf.
    /// </summary>
    [Fact]
    public void Host_payload_without_embedded_rpcId_parses_real_wire_shape()
    {
        const string text =
            "{\"type\":\"server-request\",\"rpcId\":\"r9\",\"method\":\"host/session-status\"," +
            "\"payload\":{\"type\":\"host/session-status\",\"sessionId\":\"s1\",\"running\":true}}";

        bool ok = DownstreamStreams.TryParseFrame<HostFrame>(text, Log, out var rpcId, out var frame);

        Assert.True(ok);
        Assert.Equal(RpcId.Of("r9"), rpcId);
        var status = Assert.IsType<HostSessionStatusFrame>(frame);
        Assert.True(status.Running);
    }

    [Fact]
    public void Foreign_envelope_type_is_dropped_not_parsed()
    {
        const string text = "{\"type\":\"server-response\",\"rpcId\":\"r1\",\"result\":{\"ok\":true}}";

        Assert.False(DownstreamStreams.TryParseFrame<MuxFrame>(text, Log, out _, out _));
    }

    [Fact]
    public void Envelope_missing_rpcId_is_dropped_without_throwing()
    {
        // Regression: GetProperty("rpcId") used to throw KeyNotFoundException, which escaped
        // the JsonException-only catch and could kill the whole receive loop.
        const string text =
            "{\"type\":\"server-request\",\"method\":\"session/event\"," +
            "\"payload\":{\"type\":\"stream/error\",\"error\":{\"code\":\"internal\",\"message\":\"x\"}}}";

        Assert.False(DownstreamStreams.TryParseFrame<MuxFrame>(text, Log, out _, out _));
    }

    [Fact]
    public void Envelope_missing_payload_is_dropped_without_throwing()
    {
        const string text = "{\"type\":\"server-request\",\"rpcId\":\"r1\",\"method\":\"m\"}";

        Assert.False(DownstreamStreams.TryParseFrame<MuxFrame>(text, Log, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"type\":\"server-request\",\"rpcId\":\"r1\",\"payload\":{\"type\":\"session/unknown-forever\"}}")]
    public void Malformed_or_unroutable_text_is_dropped_not_fatal(string text)
    {
        // One corrupt frame must not kill the stream — but (post-fix) every drop is logged
        // with a reason and a counter, so contract drift can never deafen a stream silently.
        Assert.False(DownstreamStreams.TryParseFrame<MuxFrame>(text, Log, out _, out _));
    }

    // ---------------------------------------------------------------------------------------
    // rpcId backfill
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Backfill_sets_rpcId_on_mux_frame_when_absent()
    {
        MuxFrame frame = new SessionSubscribedFrame { SessionId = "s1", LastSeq = 1 };

        DownstreamStreams.BackfillRpcId(frame, RpcId.Of("r1"));

        Assert.Equal(RpcId.Of("r1"), frame.RpcId);
    }

    [Fact]
    public void Backfill_sets_rpcId_on_host_frame_when_absent()
    {
        // The host branch was entirely missing before the P0 fix — host frames parsed (after
        // the contract fix) but never received the envelope's rpcId.
        HostFrame frame = new HostSessionStatusFrame { SessionId = "s1", Running = false };

        DownstreamStreams.BackfillRpcId(frame, RpcId.Of("r2"));

        Assert.Equal(RpcId.Of("r2"), frame.RpcId);
    }

    [Fact]
    public void Backfill_does_not_overwrite_existing_rpcId()
    {
        MuxFrame frame = new SessionSubscribedFrame
        {
            RpcId = RpcId.Of("original"),
            SessionId = "s1",
            LastSeq = 1,
        };

        DownstreamStreams.BackfillRpcId(frame, RpcId.Of("envelope"));

        Assert.Equal(RpcId.Of("original"), frame.RpcId);
    }

    [Fact]
    public void Backfill_is_noop_for_unrelated_objects()
    {
        var stranger = new object();

        DownstreamStreams.BackfillRpcId(stranger, RpcId.Of("x"));

        Assert.Same(stranger, stranger); // reached without throwing / mutating
    }
}
