using Dsh.Contract.Rpc;

namespace Dsh.Client;

/// <summary>
/// Minimal seam exposing the client-response carrier (<c>POST /api/respond</c>). Isolated so
/// <see cref="Dsh.App.InteractionCoordinator"/> can be unit-tested without a live host.
/// </summary>
public interface IRespondClient
{
    /// <summary>Post a client-response to an answerable server-request; echoes the rpcId.</summary>
    Task<RpcReceipt> Respond(RpcId rpcId, object? result, CancellationToken ct = default);
}
