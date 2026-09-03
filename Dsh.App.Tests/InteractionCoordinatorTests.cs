using Dsh.Client;
using Dsh.Contract.Frames;
using Dsh.Contract.Rpc;
using Xunit;

namespace Dsh.App.Tests;

/// <summary>
/// State-machine tests for <see cref="InteractionCoordinator"/> (no HTTP): Open records a
/// pending interaction keyed by rpcId; Settle removes it on the matching resolved frame.
/// </summary>
public class InteractionCoordinatorTests
{
    private static InteractionCoordinator Create() =>
        new(new FakeRespondClient());

    [Fact]
    public void Open_approval_records_pending_keyed_by_rpcId()
    {
        var c = Create();
        var rpcId = RpcId.Of("r-1");

        c.Open(new ApprovalRequestedFrame
        {
            RpcId = rpcId,
            SessionId = "s1",
            ApprovalId = "app-1",
            ToolName = "bash",
        });

        var pending = Assert.Single(c.Pending);
        Assert.Equal(rpcId, pending.Key);
        Assert.Equal("approval", pending.Value.Kind);
        Assert.Equal("app-1", pending.Value.ApprovalId);
    }

    [Fact]
    public void Settle_question_removes_matching_pending()
    {
        var c = Create();
        var rpcId = RpcId.Of("r-2");

        c.Open(new QuestionRequestedFrame { RpcId = rpcId, SessionId = "s1", Questions = [] });
        Assert.Single(c.Pending);

        c.Settle(new QuestionResolvedFrame { RpcId = rpcId, SessionId = "s1", QuestionRpcId = rpcId, Outcome = "answered" });
        Assert.Empty(c.Pending);
    }

    [Fact]
    public void Settle_approval_matches_by_approvalId_not_rpcId()
    {
        var c = Create();
        var rpcId = RpcId.Of("r-3");

        c.Open(new ApprovalRequestedFrame { RpcId = rpcId, SessionId = "s1", ApprovalId = "app-9", ToolName = "bash" });
        Assert.Single(c.Pending);

        // The resolved frame carries a different rpcId but the same approvalId; it still settles.
        c.Settle(new ApprovalResolvedFrame { RpcId = RpcId.Of("r-other"), SessionId = "s1", ApprovalId = "app-9", Outcome = "allowed-once" });
        Assert.Empty(c.Pending);
    }
}

/// <summary>Minimal <see cref="IRespondClient"/> stand-in (no HTTP calls in these tests).</summary>
file sealed class FakeRespondClient : IRespondClient
{
    public Task<RpcReceipt> Respond(RpcId rpcId, object? result, CancellationToken ct = default) =>
        Task.FromResult(RpcReceipt.Ok());
}
