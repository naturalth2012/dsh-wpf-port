using System.Text.Json.Serialization;
using Dsh.Contract.Methods;
using Dsh.Contract.Rpc;

namespace Dsh.Contract.Frames;

/// <summary>
/// Mux stream frames, mirroring <c>MuxFrame</c> in events.ts: raw session-event passthrough
/// + control frames + approval/question frames. A frame is the <c>payload</c> slot of a
/// downstream <c>ServerRequest</c> (SSE <c>data:</c> line); the payload carries the <c>type</c>
/// discriminator, so deserialization keys on it via <c>[JsonPolymorphic]</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SessionEventFrame), "session/event")]
[JsonDerivedType(typeof(SessionSubscribedFrame), "session/subscribed")]
[JsonDerivedType(typeof(ApprovalRequestedFrame), "approval/requested")]
[JsonDerivedType(typeof(ApprovalResolvedFrame), "approval/resolved")]
[JsonDerivedType(typeof(QuestionRequestedFrame), "question/requested")]
[JsonDerivedType(typeof(QuestionResolvedFrame), "question/resolved")]
[JsonDerivedType(typeof(SessionQueueFrame), "session/queue")]
[JsonDerivedType(typeof(SessionJobsFrame), "session/jobs")]
[JsonDerivedType(typeof(SessionProjectionFrame), "session/projection")]
[JsonDerivedType(typeof(StreamErrorFrame), "stream/error")]
public abstract record MuxFrame
{
    /// <summary>Correlation id minted by the host for this push (echo on answerable frames).
    /// The host carries <c>rpcId</c> only on the enclosing server-request envelope, NOT inside the
    /// frame payload, so this is optional here and backfilled from the envelope by the client.</summary>
    public RpcId? RpcId { get; set; }
}

/// <summary>Raw session-event passthrough (the client folds it into the surface).</summary>
public sealed record SessionEventFrame : MuxFrame
{
    public required string SessionId { get; init; }
    /// <summary>Raw SessionEvent payload; folded by the client's shared fold.</summary>
    public object? Event { get; init; }
    /// <summary>Optional host-computed render intent.</summary>
    public object? View { get; init; }
}

/// <summary>Subscription baseline after open; lastSeq is the seq of the last committed event.</summary>
public sealed record SessionSubscribedFrame : MuxFrame
{
    public required string SessionId { get; init; }
    public required long LastSeq { get; init; }
}

/// <summary>Answerable server-request: a pending approval.</summary>
public sealed record ApprovalRequestedFrame : MuxFrame
{
    public required string SessionId { get; init; }
    public required string ApprovalId { get; init; }
    public required string ToolName { get; init; }
    public string? CallId { get; init; }
    public string? Reason { get; init; }
}

/// <summary>Pure push: the approval outcome settled.</summary>
public sealed record ApprovalResolvedFrame : MuxFrame
{
    public required string SessionId { get; init; }
    public required string ApprovalId { get; init; }
    public required string Outcome { get; init; }
}

/// <summary>Answerable server-request: an ask-user question batch.</summary>
public sealed record QuestionRequestedFrame : MuxFrame
{
    public required string SessionId { get; init; }
    public required QuestionItem[] Questions { get; init; }
}

/// <summary>Pure push: the question outcome settled.</summary>
public sealed record QuestionResolvedFrame : MuxFrame
{
    public required string SessionId { get; init; }
    public required RpcId QuestionRpcId { get; init; }
    public required string Outcome { get; init; }
}

/// <summary>Complete transient inbox state after every enqueue/mutation/claim/discard.</summary>
public sealed record SessionQueueFrame : MuxFrame
{
    public required string SessionId { get; init; }
    public required QueuedInboxItem[] Items { get; init; }
}

/// <summary>Complete set of background jobs this session can see (whole snapshot).</summary>
public sealed record SessionJobsFrame : MuxFrame
{
    public required string SessionId { get; init; }
    public required JobInfo[] Jobs { get; init; }
}

/// <summary>One projection unit's finished value changed (higher-seq-wins on the client).</summary>
public sealed record SessionProjectionFrame : MuxFrame
{
    public required string SessionId { get; init; }
    public required string Key { get; init; }
    public object? Value { get; init; }
    public required long Seq { get; init; }
}

/// <summary>Stream-level error (shared by both mux and host streams).</summary>
public sealed record StreamErrorFrame : MuxFrame
{
    public required RpcError Error { get; init; }
}
