namespace Dsh.Contract.Frames;

/// <summary>
/// Registry mapping a downstream stream frame's wire <c>type</c> literal (the
/// <c>ServerRequest.method</c> value) to its CLR body type. This is the single source of
/// truth the client's stream reader uses to deserialize a frame body, and the target of the
/// contract-consistency test that asserts the literal set matches events.ts.
/// </summary>
public static class FrameRegistry
{
    /// <summary>All mux-stream frame type literals, in declaration order.</summary>
    public static readonly string[] MuxTypes =
    {
        "session/event", "session/subscribed", "approval/requested", "approval/resolved",
        "question/requested", "question/resolved", "session/queue", "session/jobs",
        "session/projection", "stream/error",
    };

    /// <summary>All host-stream frame type literals, in declaration order.</summary>
    public static readonly string[] HostTypes =
    {
        "host/session-added", "host/session-removed", "host/session-status",
        "host/agent-error", "host/workspace-changed", "host/workspace-removed",
        "host/workspace-order-changed", "host/archived-sessions-changed",
        "host/remote-event", "stream/error",
    };

    /// <summary>Resolve a mux frame type literal to its CLR body type; null if unknown.</summary>
    public static Type? MuxBodyType(string type) => type switch
    {
        "session/event" => typeof(SessionEventFrame),
        "session/subscribed" => typeof(SessionSubscribedFrame),
        "approval/requested" => typeof(ApprovalRequestedFrame),
        "approval/resolved" => typeof(ApprovalResolvedFrame),
        "question/requested" => typeof(QuestionRequestedFrame),
        "question/resolved" => typeof(QuestionResolvedFrame),
        "session/queue" => typeof(SessionQueueFrame),
        "session/jobs" => typeof(SessionJobsFrame),
        "session/projection" => typeof(SessionProjectionFrame),
        "stream/error" => typeof(StreamErrorFrame),
        _ => null,
    };

    /// <summary>Resolve a host frame type literal to its CLR body type; null if unknown.</summary>
    public static Type? HostBodyType(string type) => type switch
    {
        "host/session-added" => typeof(HostSessionAddedFrame),
        "host/session-removed" => typeof(HostSessionRemovedFrame),
        "host/session-status" => typeof(HostSessionStatusFrame),
        "host/agent-error" => typeof(HostAgentErrorFrame),
        "host/workspace-changed" => typeof(HostWorkspaceChangedFrame),
        "host/workspace-removed" => typeof(HostWorkspaceRemovedFrame),
        "host/workspace-order-changed" => typeof(HostWorkspaceOrderChangedFrame),
        "host/archived-sessions-changed" => typeof(HostArchivedSessionsChangedFrame),
        "host/remote-event" => typeof(HostRemoteEventFrame),
        "stream/error" => typeof(HostStreamErrorFrame),
        _ => null,
    };
}
