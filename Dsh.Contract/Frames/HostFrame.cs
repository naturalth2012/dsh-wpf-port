using System.Text.Json.Serialization;
using Dsh.Contract.Rpc;

namespace Dsh.Contract.Frames;

/// <summary>
/// Host stream frames, mirroring <c>HostFrame</c> in events.ts: session create/destroy,
/// running-status flips, workspace mutation pushes, and agent failures with no turn position.
/// Wire form matches <see cref="MuxFrame"/>: payload carries the <c>type</c> discriminator.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HostSessionAddedFrame), "host/session-added")]
[JsonDerivedType(typeof(HostSessionRemovedFrame), "host/session-removed")]
[JsonDerivedType(typeof(HostSessionStatusFrame), "host/session-status")]
[JsonDerivedType(typeof(HostAgentErrorFrame), "host/agent-error")]
[JsonDerivedType(typeof(HostWorkspaceChangedFrame), "host/workspace-changed")]
[JsonDerivedType(typeof(HostWorkspaceRemovedFrame), "host/workspace-removed")]
[JsonDerivedType(typeof(HostWorkspaceOrderChangedFrame), "host/workspace-order-changed")]
[JsonDerivedType(typeof(HostArchivedSessionsChangedFrame), "host/archived-sessions-changed")]
[JsonDerivedType(typeof(HostRemoteEventFrame), "host/remote-event")]
[JsonDerivedType(typeof(HostStreamErrorFrame), "stream/error")]
public abstract record HostFrame
{
    /// <summary>Correlation id minted by the host for this push (echo on answerable frames).
    /// The host carries <c>rpcId</c> only on the enclosing server-request envelope, NOT inside the
    /// frame payload, so this is optional here and backfilled from the envelope by the client.</summary>
    public RpcId? RpcId { get; set; }
}

/// <summary>A session was created; carries lineage anchor, origin, cwd, and blank bit.</summary>
public sealed record HostSessionAddedFrame : HostFrame
{
    public required string SessionId { get; init; }
    public required bool Blank { get; init; }
    public string? ParentSessionId { get; init; }
    public string? Origin { get; init; }
    public string? Cwd { get; init; }
    public string? AgentPreset { get; init; }
}

/// <summary>A session was destroyed.</summary>
public sealed record HostSessionRemovedFrame : HostFrame
{
    public required string SessionId { get; init; }
}

/// <summary>A session's running status flipped.</summary>
public sealed record HostSessionStatusFrame : HostFrame
{
    public required string SessionId { get; init; }
    public required bool Running { get; init; }
}

/// <summary>The only outlet for live failures with no turn position.</summary>
public sealed record HostAgentErrorFrame : HostFrame
{
    public required string SessionId { get; init; }
    public required string Message { get; init; }
}

/// <summary>Full new workspace snapshot after every durable workspace mutation.</summary>
public sealed record HostWorkspaceChangedFrame : HostFrame
{
    public required object Workspace { get; init; }
}

/// <summary>Committed registration-deletion increment (never directory/session-log deletion).</summary>
public sealed record HostWorkspaceRemovedFrame : HostFrame
{
    public required string WorkspaceId { get; init; }
}

/// <summary>Complete durable registry order after a reorder.</summary>
public sealed record HostWorkspaceOrderChangedFrame : HostFrame
{
    public required string[] WorkspaceIds { get; init; }
}

/// <summary>Full registry archive set after every durable change.</summary>
public sealed record HostArchivedSessionsChangedFrame : HostFrame
{
    public required string[] ArchivedSessionIds { get; init; }
}

/// <summary>One allowlisted host cordis event forwarded verbatim.</summary>
public sealed record HostRemoteEventFrame : HostFrame
{
    public required string Event { get; init; }
    public required object?[] Args { get; init; }
}

/// <summary>Stream-level error on the host stream.</summary>
public sealed record HostStreamErrorFrame : HostFrame
{
    public required RpcError Error { get; init; }
}
