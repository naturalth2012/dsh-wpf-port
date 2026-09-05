using System.Text.Json;
using Dsh.Contract.Frames;
using Dsh.Contract.Rpc;
using Xunit;

namespace Dsh.Contract.Tests;

/// <summary>
/// Regression gate for the downlink frame contract. The mux (session-event) and host streams
/// carry the <c>type</c> discriminator in the payload slot of a <see cref="ServerRequest"/>;
/// <see cref="MuxFrame"/> / <see cref="HostFrame"/> are <c>[JsonPolymorphic]</c> roots that must
/// route every known <c>type</c> literal to its concrete derived record (never silently to a
/// base/unknown type). A drift in a derived-type <c>[JsonDerivedType]</c> tag, or a field rename,
/// would otherwise surface as a blank chat in the GUI with no compile-time signal. These tests
/// replay real host JSON bytes for all derived types and assert both routing and field binding.
/// </summary>
public class MuxFrameWireTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // -----------------------------------------------------------------------------------------
    // Mux frame discriminator routing
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("session/event", typeof(SessionEventFrame))]
    [InlineData("session/subscribed", typeof(SessionSubscribedFrame))]
    [InlineData("approval/requested", typeof(ApprovalRequestedFrame))]
    [InlineData("approval/resolved", typeof(ApprovalResolvedFrame))]
    [InlineData("question/requested", typeof(QuestionRequestedFrame))]
    [InlineData("question/resolved", typeof(QuestionResolvedFrame))]
    [InlineData("session/queue", typeof(SessionQueueFrame))]
    [InlineData("session/jobs", typeof(SessionJobsFrame))]
    [InlineData("session/projection", typeof(SessionProjectionFrame))]
    [InlineData("stream/error", typeof(StreamErrorFrame))]
    public void MuxFrame_routes_every_known_type_to_concrete_record(string type, Type expected)
    {
        // Minimal valid payload for each type; only the routing + fixed fields matter here.
        var json = type switch
        {
            "session/event" => $"{{\"type\":\"{type}\",\"sessionId\":\"s1\",\"event\":{{\"type\":\"user/message\"}}}}",
            "session/subscribed" => $"{{\"type\":\"{type}\",\"sessionId\":\"s1\",\"lastSeq\":42}}",
            "approval/requested" => $"{{\"type\":\"{type}\",\"sessionId\":\"s1\",\"approvalId\":\"a1\",\"toolName\":\"bash\"}}",
            "approval/resolved" => $"{{\"type\":\"{type}\",\"sessionId\":\"s1\",\"approvalId\":\"a1\",\"outcome\":\"approved\"}}",
            "question/requested" => $"{{\"type\":\"{type}\",\"sessionId\":\"s1\",\"questions\":[{{\"id\":\"q1\",\"question\":\"pick one\"}}]}}",
            "question/resolved" => $"{{\"type\":\"{type}\",\"sessionId\":\"s1\",\"questionRpcId\":\"q1\",\"outcome\":\"answered\"}}",
            "session/queue" => $"{{\"type\":\"{type}\",\"sessionId\":\"s1\",\"items\":[]}}",
            "session/jobs" => $"{{\"type\":\"{type}\",\"sessionId\":\"s1\",\"jobs\":[]}}",
            "session/projection" => $"{{\"type\":\"{type}\",\"sessionId\":\"s1\",\"key\":\"title\",\"seq\":5}}",
            "stream/error" => $"{{\"type\":\"{type}\",\"error\":{{\"code\":\"bad-request\",\"message\":\"x\"}}}}",
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        var frame = JsonSerializer.Deserialize<MuxFrame>(json, Options);

        Assert.NotNull(frame);
        Assert.Equal(expected, frame.GetType());
        // Real wire payloads do NOT embed rpcId — the host carries it only on the enclosing
        // server-request envelope, and the client backfills it (DownstreamStreams.BackfillRpcId,
        // covered in Dsh.Client.Tests). Fixtures used to embed "rpcId":"r1" here, a shape the
        // host never sends, which is exactly what let the HostFrame `required RpcId` drift pass
        // every test while the production host stream ran deaf.
        Assert.Null(frame.RpcId);
    }

    [Fact]
    public void SessionEventFrame_binds_sessionId_and_event_payload()
    {
        const string json =
            "{\"type\":\"session/event\",\"sessionId\":\"sess-abc\"," +
            "\"event\":{\"type\":\"assistant/chunk\",\"seq\":3,\"time\":123,\"data\":{\"chunk\":{\"type\":\"text-delta\",\"text\":\"hi\"}}}}";

        var frame = JsonSerializer.Deserialize<MuxFrame>(json, Options) as SessionEventFrame;

        Assert.NotNull(frame);
        Assert.Equal("sess-abc", frame.SessionId);
        // Event is typed object? in the contract; STJ keeps it as a JsonElement at this layer
        // (the client's SessionFold reads it via JsonElement). The discriminator must not be lost.
        Assert.NotNull(frame.Event);
        Assert.True(frame.Event is JsonElement, "session/event `event` slot should round-trip as JsonElement");
    }

    [Fact]
    public void SessionSubscribedFrame_binds_lastSeq()
    {
        const string json =
            "{\"type\":\"session/subscribed\",\"sessionId\":\"s1\",\"lastSeq\":17}";

        var frame = JsonSerializer.Deserialize<MuxFrame>(json, Options) as SessionSubscribedFrame;

        Assert.NotNull(frame);
        Assert.Equal("s1", frame.SessionId);
        Assert.Equal(17L, frame.LastSeq);
    }

    [Fact]
    public void ApprovalRequestedFrame_binds_all_fixed_fields()
    {
        const string json =
            "{\"type\":\"approval/requested\",\"sessionId\":\"s1\"," +
            "\"approvalId\":\"a1\",\"toolName\":\"bash\",\"callId\":\"c1\",\"reason\":\"needs sudo\"}";

        var frame = JsonSerializer.Deserialize<MuxFrame>(json, Options) as ApprovalRequestedFrame;

        Assert.NotNull(frame);
        Assert.Equal("a1", frame.ApprovalId);
        Assert.Equal("bash", frame.ToolName);
        Assert.Equal("c1", frame.CallId);
        Assert.Equal("needs sudo", frame.Reason);
    }

    [Fact]
    public void QuestionRequestedFrame_binds_questions_array()
    {
        const string json =
            "{\"type\":\"question/requested\",\"sessionId\":\"s1\"," +
            "\"questions\":[{\"id\":\"q1\",\"question\":\"pick one\"},{\"id\":\"q2\",\"question\":\"pick two\"}]}";

        var frame = JsonSerializer.Deserialize<MuxFrame>(json, Options) as QuestionRequestedFrame;

        Assert.NotNull(frame);
        Assert.Equal(2, frame.Questions.Length);
        Assert.Equal("q1", frame.Questions[0].Id);
        Assert.Equal("q2", frame.Questions[1].Id);
    }

    [Fact]
    public void QuestionItem_requires_question_field_per_host_schema()
    {
        // Regression guard: the host schema marks `question` required; a payload missing it must
        // surface as a hard deserialization failure (not a silently-defaulted item), so the GUI
        // never renders an unanswerable blank question card.
        const string json =
            "{\"type\":\"question/requested\",\"sessionId\":\"s1\"," +
            "\"questions\":[{\"id\":\"q1\"}]}";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MuxFrame>(json, Options));
    }

    [Fact]
    public void SessionQueueFrame_binds_items_snapshot()
    {
        const string json =
            "{\"type\":\"session/queue\",\"sessionId\":\"s1\"," +
            "\"items\":[{\"id\":\"i1\",\"placement\":\"head\",\"message\":\"m1\"}]}";

        var frame = JsonSerializer.Deserialize<MuxFrame>(json, Options) as SessionQueueFrame;

        Assert.NotNull(frame);
        Assert.Single(frame.Items);
        Assert.Equal("i1", frame.Items[0].Id);
        Assert.Equal("head", frame.Items[0].Placement);
        // Message is typed JsonElement? in the contract (raw passthrough like SessionEventFrame.Event).
        Assert.True(frame.Items[0].Message is JsonElement m && m.GetString() == "m1",
            "queue item message should round-trip to string 'm1'");
    }

    [Fact]
    public void SessionProjectionFrame_binds_key_and_seq()
    {
        const string json =
            "{\"type\":\"session/projection\",\"sessionId\":\"s1\"," +
            "\"key\":\"title\",\"seq\":9}";

        var frame = JsonSerializer.Deserialize<MuxFrame>(json, Options) as SessionProjectionFrame;

        Assert.NotNull(frame);
        Assert.Equal("title", frame.Key);
        Assert.Equal(9L, frame.Seq);
        // Value binding is covered by SessionProjectionFrame_binds_value_payload (object? slot).
    }

    [Fact]
    public void SessionProjectionFrame_binds_value_payload()
    {
        const string json =
            "{\"type\":\"session/projection\",\"sessionId\":\"s1\"," +
            "\"key\":\"title\",\"seq\":9,\"value\":\"My Run\"}";

        var frame = JsonSerializer.Deserialize<MuxFrame>(json, Options) as SessionProjectionFrame;

        Assert.NotNull(frame);
        Assert.Equal("My Run", frame.Value?.ToString());
    }

    [Fact]
    public void StreamErrorFrame_binds_error_code()
    {
        const string json =
            "{\"type\":\"stream/error\"," +
            "\"error\":{\"code\":\"bad-request\",\"message\":\"invalid frame\"}}";

        var frame = JsonSerializer.Deserialize<MuxFrame>(json, Options) as StreamErrorFrame;

        Assert.NotNull(frame);
        Assert.Equal(RpcErrorCode.BadRequest, frame.Error.Code);
        Assert.Equal("invalid frame", frame.Error.Message);
    }

    // -----------------------------------------------------------------------------------------
    // Mux frame serialize → host-acceptable wire shape
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void MuxFrame_serializes_type_literal_for_host()
    {
        // The host's muxFrameSchema rejects envelopes whose `type` literal differs from the
        // registered tag. STJ must emit the wire literal, not the C# type name.
        MuxFrame frame = new SessionSubscribedFrame
        {
            RpcId = RpcId.Of("r1"),
            SessionId = "s1",
            LastSeq = 1,
        };

        string wire = JsonSerializer.Serialize(frame, Options);

        Assert.Contains("\"type\":\"session/subscribed\"", wire);
        Assert.Contains("\"lastSeq\":1", wire);
    }

    // -----------------------------------------------------------------------------------------
    // Host frame discriminator routing
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("host/session-added", typeof(HostSessionAddedFrame))]
    [InlineData("host/session-removed", typeof(HostSessionRemovedFrame))]
    [InlineData("host/session-status", typeof(HostSessionStatusFrame))]
    [InlineData("host/agent-error", typeof(HostAgentErrorFrame))]
    [InlineData("host/workspace-changed", typeof(HostWorkspaceChangedFrame))]
    [InlineData("host/workspace-removed", typeof(HostWorkspaceRemovedFrame))]
    [InlineData("host/workspace-order-changed", typeof(HostWorkspaceOrderChangedFrame))]
    [InlineData("host/archived-sessions-changed", typeof(HostArchivedSessionsChangedFrame))]
    [InlineData("host/remote-event", typeof(HostRemoteEventFrame))]
    [InlineData("stream/error", typeof(HostStreamErrorFrame))]
    public void HostFrame_routes_every_known_type_to_concrete_record(string type, Type expected)
    {
        var json = type switch
        {
            "host/session-added" => $"{{\"type\":\"{type}\",\"sessionId\":\"s1\",\"blank\":true}}",
            "host/session-removed" => $"{{\"type\":\"{type}\",\"sessionId\":\"s1\"}}",
            "host/session-status" => $"{{\"type\":\"{type}\",\"sessionId\":\"s1\",\"running\":true}}",
            "host/agent-error" => $"{{\"type\":\"{type}\",\"sessionId\":\"s1\",\"message\":\"boom\"}}",
            "host/workspace-changed" => $"{{\"type\":\"{type}\",\"workspace\":{{\"workspaceId\":\"w1\"}}}}",
            "host/workspace-removed" => $"{{\"type\":\"{type}\",\"workspaceId\":\"w1\"}}",
            "host/workspace-order-changed" => $"{{\"type\":\"{type}\",\"workspaceIds\":[\"w1\",\"w2\"]}}",
            "host/archived-sessions-changed" => $"{{\"type\":\"{type}\",\"archivedSessionIds\":[\"s9\"]}}",
            "host/remote-event" => $"{{\"type\":\"{type}\",\"event\":\"did-x\",\"args\":[1,\"a\"]}}",
            "stream/error" => $"{{\"type\":\"{type}\",\"error\":{{\"code\":\"internal\",\"message\":\"x\"}}}}",
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        var frame = JsonSerializer.Deserialize<HostFrame>(json, Options);

        Assert.NotNull(frame);
        Assert.Equal(expected, frame.GetType());
    }

    [Fact]
    public void HostSessionAddedFrame_binds_lineage_fields()
    {
        const string json =
            "{\"type\":\"host/session-added\",\"sessionId\":\"s1\"," +
            "\"blank\":true,\"parentSessionId\":\"p1\",\"origin\":\"fork\",\"cwd\":\"D:\\\\x\"," +
            "\"agentPreset\":\"general\"}";

        var frame = JsonSerializer.Deserialize<HostFrame>(json, Options) as HostSessionAddedFrame;

        Assert.NotNull(frame);
        Assert.True(frame.Blank);
        Assert.Equal("p1", frame.ParentSessionId);
        Assert.Equal("fork", frame.Origin);
        Assert.Equal("D:\\x", frame.Cwd);
        Assert.Equal("general", frame.AgentPreset);
    }

    [Fact]
    public void HostSessionStatusFrame_binds_running_flag()
    {
        const string json = "{\"type\":\"host/session-status\",\"sessionId\":\"s1\",\"running\":false}";

        var frame = JsonSerializer.Deserialize<HostFrame>(json, Options) as HostSessionStatusFrame;

        Assert.NotNull(frame);
        Assert.False(frame.Running);
    }

    [Fact]
    public void HostStreamErrorFrame_binds_error_code()
    {
        const string json =
            "{\"type\":\"stream/error\"," +
            "\"error\":{\"code\":\"internal\",\"message\":\"closed\"}}";

        var frame = JsonSerializer.Deserialize<HostFrame>(json, Options) as HostStreamErrorFrame;

        Assert.NotNull(frame);
        Assert.Equal(RpcErrorCode.Internal, frame.Error.Code);
    }
}