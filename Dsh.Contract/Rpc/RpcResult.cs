namespace Dsh.Contract.Rpc;

/// <summary>
/// Business success/failure result, mirroring <c>RpcResult&lt;T&gt;</c> in rpc.ts:
/// the result slot of a unary response; methods never throw business errors.
/// </summary>
public readonly record struct RpcResult<T>
{
    public bool Ok { get; }
    public T? Value { get; }
    public RpcError? Error { get; }

    private RpcResult(bool ok, T? value, RpcError? error)
    {
        Ok = ok;
        Value = value;
        Error = error;
    }

    public static RpcResult<T> Success(T value) => new(true, value, null);

    public static RpcResult<T> Failure(RpcError error) => new(false, default, error);
}
