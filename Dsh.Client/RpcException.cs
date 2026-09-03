using Dsh.Contract.Rpc;

namespace Dsh.Client;

/// <summary>
/// Exception surfaced for a business RPC error (unary call returned <c>{ ok:false }</c>).
/// Carries the full <see cref="RpcError"/> so callers can branch on <see cref="RpcErrorCode"/>
/// (e.g. <c>settings-conflict</c>, <c>agent-busy</c>) rather than string-matching.
/// </summary>
public sealed class RpcException : Exception
{
    public RpcError Error { get; }

    public RpcException(RpcError error)
        : base(error.Message)
    {
        Error = error;
    }
}
