using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dsh.Contract.Frames;
using Dsh.Contract.Methods;
using Dsh.Contract.Projections;
using Dsh.Contract.Rpc;
using Xunit;

namespace Dsh.Contract.Tests;

/// <summary>
/// Contract-consistency tests: assert that the C# contract layer exactly mirrors the
/// authoritative host source (rpc-map.ts method keys, rpc.ts error codes, events.ts frame
/// type literals, session-projection keys). A failure means a drift between the C# contract
/// and the host source — the trigger for os/05-contract-sync.md regression.
/// </summary>
public class ContractConsistencyTests
{
    // ── Method registry ────────────────────────────────────────────────────

    [Fact]
    public void MethodRegistry_matches_host_55_keys()
    {
        string[] expected =
        {
            // session (12)
            "session.list", "session.search", "session.create", "session.history",
            "session.models", "session.selectModel", "session.rename", "session.fork",
            "session.prompt", "session.attachment", "session.updateQueue", "session.cancel",
            // subagent (4)
            "subagent.list", "subagent.history", "subagent.prompt", "subagent.interrupt",
            // host (5)
            "host.describe", "host.pickDirectory", "host.listDirectory",
            "host.createDirectory", "host.openPath",
            // workspace (7)
            "workspace.list", "workspace.create", "workspace.rename", "workspace.delete",
            "workspace.insertBefore", "workspace.insertSessionBefore", "workspace.archiveSession",
            // skill (1)
            "skill.list",
            // agentPreset (6)
            "agentPreset.list", "agentPreset.select", "agentPreset.read", "agentPreset.copy",
            "agentPreset.openDocument", "agentPreset.remove",
            // goal (6)
            "goal.create", "goal.edit", "goal.pause", "goal.resume", "goal.complete", "goal.clear",
            // settings (5)
            "settings.describe", "settings.openDocument", "settings.update", "settings.replace", "settings.mutate",
            // credentials (3)
            "credentials.describe", "credentials.set", "credentials.unset",
            // llm (3)
            "llm.providers", "llm.models", "llm.discoverModels",
            // messageFeedback (3, P2-4)
            "messageFeedback.list", "messageFeedback.put", "messageFeedback.delete",
        };

        Assert.Equal(55, RpcMethods.All.Length);
        Assert.Equal(55, RpcMethods.All.Distinct().Count()); // no duplicates
        Assert.Equal(expected.OrderBy(x => x), RpcMethods.All.OrderBy(x => x));
    }

    // ── Error codes ────────────────────────────────────────────────────────

    [Fact]
    public void RpcErrorCode_enum_matches_host_40_codes()
    {
        string[] expected =
        {
            "bad-request", "cancelled", "session-not-found", "model-unavailable",
            "session-conflict", "invalid-time-zone", "workspace-attach-failed", "workspace-not-found",
            "workspace-invalid-path", "workspace-name-conflict", "workspace-move-invalid",
            "directory-unreadable", "directory-exists", "directory-create-failed",
            "directory-picker-unavailable", "agent-preset-read-only", "agent-preset-locked",
            "agent-preset-conflict", "agent-preset-not-found", "agent-preset-invalid",
            "agent-busy", "attachment-error", "queue-item-not-found", "steer-unavailable",
            "command-error", "unknown-command", "settings-rejected", "settings-not-exposed",
            "settings-conflict", "credential-rejected", "model-discovery-failed", "title-invalid",
            "fork-unavailable", "subagent-parent-unavailable", "subagent-not-found",
            "subagent-catalog-diagnostic", "subagent-not-resumable", "subagent-unauthorized",
            "subagent-delivery-unavailable", "internal",
        };

        var wireNames = Enum.GetValues<RpcErrorCode>()
            .Select(code =>
            {
                var name = code.GetType().GetField(code.ToString())!
                    .GetCustomAttribute<JsonPropertyNameAttribute>()!.Name;
                return name;
            })
            .OrderBy(x => x)
            .ToArray();

        Assert.Equal(40, wireNames.Length);
        Assert.Equal(expected.OrderBy(x => x), wireNames);
    }

    // ── Frames ─────────────────────────────────────────────────────────────

    [Fact]
    public void MuxFrame_variants_match_host_10_types()
    {
        string[] expected =
        {
            "session/event", "session/subscribed", "approval/requested", "approval/resolved",
            "question/requested", "question/resolved", "session/queue", "session/jobs",
            "session/projection", "stream/error",
        };

        // 1) The [JsonDerivedType] discriminators on the base type (the real wire key).
        var discriminators = GetDiscriminators<MuxFrame>();
        Assert.Equal(expected.OrderBy(x => x), discriminators.OrderBy(x => x));

        // 2) The FrameRegistry literal list must agree with the attributes (no drift).
        Assert.Equal(expected.OrderBy(x => x), FrameRegistry.MuxTypes.OrderBy(x => x));
    }

    [Fact]
    public void HostFrame_variants_match_host_10_types()
    {
        string[] expected =
        {
            "host/session-added", "host/session-removed", "host/session-status",
            "host/agent-error", "host/workspace-changed", "host/workspace-removed",
            "host/workspace-order-changed", "host/archived-sessions-changed",
            "host/remote-event", "stream/error",
        };

        var discriminators = GetDiscriminators<HostFrame>();
        Assert.Equal(expected.OrderBy(x => x), discriminators.OrderBy(x => x));
        Assert.Equal(expected.OrderBy(x => x), FrameRegistry.HostTypes.OrderBy(x => x));
    }

    private static string[] GetDiscriminators<TBase>()
        where TBase : class
    {
        return typeof(TBase)
            .GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(a => a.TypeDiscriminator?.ToString() ?? "")
            .ToArray();
    }

    // ── Projections ────────────────────────────────────────────────────────

    [Fact]
    public void ProjectionKeys_match_host_11_keys()
    {
        string[] expected =
        {
            "title", "sessionStats", "plan", "permissions", "goal", "todos",
            "tokenUsage", "contextPressure", "contextBreakdown", "imageLimits",
            "sessionListMetadata",
        };

        Assert.Equal(11, ProjectionKeys.All.Length);
        Assert.Equal(11, ProjectionKeys.All.Distinct().Count());
        Assert.Equal(expected.OrderBy(x => x), ProjectionKeys.All.OrderBy(x => x));
    }

}
