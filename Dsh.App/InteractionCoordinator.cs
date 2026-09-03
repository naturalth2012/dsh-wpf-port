using System.Collections.Concurrent;
using Dsh.Client;
using Dsh.Contract.Frames;
using Dsh.Contract.Rpc;

namespace Dsh.App;

/// <summary>
/// Coordinates answerable interactions (approval / ask-user question), mirroring the Web
/// client's takeover state machine: a requested frame opens a pending interaction keyed by
/// its stable rpcId; the user's answer is posted via <c>respond</c> (never a unary call);
/// the matching resolved frame settles it.
///
/// Respond payload wire shapes (approvals.schema.ts / questions.schema.ts):
///   approval: { sessionId, approvalId, outcome: 'allowed-once' | 'rejected' }
///   question: { sessionId, answer: { answers: [{ id, selected, custom? }] } }
/// The sessionId is mandatory on both; omitting it fails the host's schema.
/// </summary>
public sealed class InteractionCoordinator
{
    private readonly IRespondClient _client;
    private readonly ConcurrentDictionary<RpcId, PendingInteraction> _pending = new();

    public InteractionCoordinator(IRespondClient client) => _client = client;

    /// <summary>A pending answerable interaction: session, kind, and (for approvals) the id.</summary>
    public sealed record PendingInteraction(string SessionId, string Kind, string? ApprovalId = null);

    /// <summary>All currently pending interactions, keyed by the server-request rpcId.</summary>
    public IReadOnlyDictionary<RpcId, PendingInteraction> Pending => _pending;

    /// <summary>Open a pending interaction when an answerable frame arrives.</summary>
    public void Open(MuxFrame frame)
    {
        switch (frame)
        {
            case ApprovalRequestedFrame a:
                _pending[frame.RpcId!] = new PendingInteraction(a.SessionId, "approval", a.ApprovalId);
                break;
            case QuestionRequestedFrame q:
                _pending[frame.RpcId!] = new PendingInteraction(q.SessionId, "question");
                break;
        }
    }

    /// <summary>Settle a pending interaction when its resolved frame arrives.</summary>
    public void Settle(MuxFrame frame)
    {
        switch (frame)
        {
            case ApprovalResolvedFrame a:
                // Approval resolves by approvalId, but the pending is keyed by rpcId; find
                // and remove the approval pending whose id matches.
                var match = _pending.FirstOrDefault(kv =>
                    kv.Value.Kind == "approval" && kv.Value.ApprovalId == a.ApprovalId);
                if (match.Key is not null)
                {
                    _pending.TryRemove(match.Key, out _);
                }
                break;
            case QuestionResolvedFrame q:
                _pending.TryRemove(q.QuestionRpcId, out _);
                break;
        }
    }

    /// <summary>Answer a pending approval; posts the <c>sessionId + approvalId + outcome</c> wire payload.</summary>
    public Task<RpcReceipt> RespondApproval(
        RpcId rpcId,
        string sessionId,
        string approvalId,
        bool allowedOnce,
        CancellationToken ct = default)
    {
        return _client.Respond(rpcId, new
        {
            sessionId,
            approvalId,
            outcome = allowedOnce ? "allowed-once" : "rejected",
        }, ct);
    }

    /// <summary>Answer a pending question batch; posts the <c>sessionId + answer</c> wire payload.</summary>
    public Task<RpcReceipt> RespondQuestions(
        RpcId rpcId,
        string sessionId,
        IReadOnlyList<QuestionAnswer> answers,
        CancellationToken ct = default)
    {
        return _client.Respond(rpcId, new
        {
            sessionId,
            answer = new
            {
                answers = answers.Select(a => new
                {
                    a.Id,
                    a.Selected,
                    Custom = a.Custom,
                }),
            },
        }, ct);
    }

    /// <summary>Clear all pending on generation teardown.</summary>
    public void Clear() => _pending.Clear();
}

/// <summary>One selected answer within a question batch (mirrors core AskUserQuestionAnswer).</summary>
public sealed record QuestionAnswer(string Id, string[] Selected, string? Custom);
