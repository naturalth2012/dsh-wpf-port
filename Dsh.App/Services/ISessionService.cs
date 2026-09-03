using Dsh.Client;
using Dsh.Contract.Methods;

namespace Dsh.App.Services;

/// <summary>
/// Session-domain application service: business methods over the unary RPC seam,
/// mirroring the Web client's session store semantics (no UI dependency). Every return
/// is a strong-typed DTO — never <c>object</c>/<c>object[]</c>, which STJ cannot bind.
/// </summary>
public interface ISessionService
{
    /// <summary>List sessions for a workspace, newest-first.</summary>
    Task<SessionListResponse> List(string? workspaceId = null, CancellationToken ct = default);

    /// <summary>List workspaces (and their session ids) as the sidebar tree backbone.</summary>
    Task<WorkspaceListResponse> ListWorkspaces(CancellationToken ct = default);

    /// <summary>Describe all settings namespaces (schema + value + revision).</summary>
    Task<SettingsDescribeResult> DescribeSettings(CancellationToken ct = default);

    /// <summary>Describe credentials by ref (status only, never values).</summary>
    Task<CredentialsDescribeResult> DescribeCredentials(IReadOnlyList<string> refs, CancellationToken ct = default);

    /// <summary>Write a credential value (one direction only; never read back).</summary>
    Task<CredentialsMutateResult> SetCredential(string ref_, string value, CancellationToken ct = default);

    /// <summary>Clear a credential.</summary>
    Task<CredentialsMutateResult> UnsetCredential(string ref_, CancellationToken ct = default);

    /// <summary>Mutate a settings namespace with CAS (expectedRevision); returns the new view.</summary>
    Task<SettingsWriteResult> MutateSettings(string ns, IReadOnlyList<SettingsPathOp> ops, long? expectedRevision, CancellationToken ct = default);

    /// <summary>Probe a provider endpoint for its model catalog.</summary>
    Task<DiscoverModelsResult> DiscoverModels(string settingsNs, string provider, string? baseUrl, string? api, CancellationToken ct = default);

    /// <summary>
    /// Load a page of a session's history. With <paramref name="beforeSeq"/> null this returns the
    /// tail (latest) page including the projection baseline; with <paramref name="beforeSeq"/> set it
    /// returns the page strictly older than that seq (the loadOlder path), used for on-demand
    /// up-scroll pagination. Mirrors <c>sessionHistoryRequestSchema</c> (beforeSeq/maxMessages).
    /// </summary>
    Task<SessionHistoryPage> GetHistory(string sessionId, int? maxMessages = null, long? beforeSeq = null, CancellationToken ct = default);

    /// <summary>Create a blank session (optionally in a workspace, at a cwd).</summary>
    Task<SessionCreated> Create(string? workspaceId = null, string? cwd = null, CancellationToken ct = default);

    /// <summary>Prompt an existing session (content parts, text/image).</summary>
    Task<SessionPromptResult> Prompt(string sessionId, IReadOnlyList<PromptPart> content, string mode = "queue", CancellationToken ct = default);

    /// <summary>Search sessions by text snippet (host caps the result set; see <c>HasMore</c>).</summary>
    Task<SessionSearchResult> Search(string query, CancellationToken ct = default);

    /// <summary>Fork a session at a completed-turn boundary (or <paramref name="atSeq"/> if given).</summary>
    Task<SessionForkResult> Fork(string sessionId, long? atSeq = null, CancellationToken ct = default);

    /// <summary>Rename a session's title; returns the normalized title and its event seq.</summary>
    Task<SessionRenamed> Rename(string sessionId, string title, CancellationToken ct = default);

    /// <summary>Fetch the model catalog for a session (current + provider groups).</summary>
    Task<ModelsResult> GetModels(string sessionId, CancellationToken ct = default);

    /// <summary>Select a model for a session.</summary>
    Task<SelectModelResult> SelectModel(string sessionId, string provider, string model, CancellationToken ct = default);

    /// <summary>List direct child subagents of a parent session.</summary>
    Task<SubagentCatalog> ListSubagents(string parentSessionId, CancellationToken ct = default);

    /// <summary>Read one child's transcript page.</summary>
    Task<SubagentHistory> GetSubagentHistory(string parentSessionId, string childSessionId, string mode, CancellationToken ct = default);

    /// <summary>Interrupt a running subagent.</summary>
    Task<SubagentInterruptReceipt> InterruptSubagent(string parentSessionId, string childSessionId, string mode, CancellationToken ct = default);

    /// <summary>Continue a subagent with a human message.</summary>
    Task<SubagentPromptReceipt> PromptSubagent(string parentSessionId, string childSessionId, string mode, IReadOnlyList<PromptPart> content, CancellationToken ct = default);

    /// <summary>Open a filesystem path with the OS default application (host.openPath).</summary>
    Task<OpenPathResult> OpenPath(string path, CancellationToken ct = default);

    /// <summary>Set feedback on a message (messageFeedback.put, P2-4/H5).</summary>
    Task<Dsh.Contract.Methods.MessageFeedbackItem> PutMessageFeedback(string sessionId, string messageId, Dsh.Contract.Methods.MessageFeedbackRating rating, string? note = null, int? ifVersion = null, CancellationToken ct = default);

    /// <summary>List feedback for a session (messageFeedback.list, P2-4/H5).</summary>
    Task<Dsh.Contract.Methods.MessageFeedbackListValue> ListMessageFeedback(string sessionId, CancellationToken ct = default);

    /// <summary>Archive a session (registry-global; it leaves grouping surfaces but keeps its log).</summary>
    Task<ArchiveSessionResult> ArchiveSession(string sessionId, CancellationToken ct = default);

    /// <summary>Pick a directory via the host (loopback Windows IFileOpenDialog); null = user cancelled.</summary>
    Task<PickDirectoryResult> PickDirectory(CancellationToken ct = default);

    /// <summary>Create a workspace by adopting an existing directory (must be on loopback).</summary>
    Task<WorkspaceCreateResult> CreateWorkspace(string path, CancellationToken ct = default);

    /// <summary>Delete a workspace registry entry (surface leaves; directory and logs untouched).</summary>
    Task<WorkspaceDeleteResult> DeleteWorkspace(string workspaceId, CancellationToken ct = default);

    /// <summary>Move a workspace before another in the durable registry order (P2-1, workspace.insertBefore).</summary>
    Task<WorkspaceView> InsertWorkspaceBefore(string workspaceId, string? beforeId, CancellationToken ct = default);

    /// <summary>Move a session before another within a workspace (P2-1, workspace.insertSessionBefore).</summary>
    Task<SessionSummary> InsertSessionBefore(string workspaceId, string sessionId, string? beforeId, CancellationToken ct = default);

    /// <summary>List skills for a session (C12 command-catalog source; names → slash commands).</summary>
    Task<SkillListResult> ListSkills(string sessionId, CancellationToken ct = default);

    /// <summary>List agent presets (C12 command-catalog source; ids/names → slash commands).</summary>
    Task<AgentPresetListResult> ListAgentPresets(CancellationToken ct = default);

    /// <summary>Edit, remove, or strictly steer one pending queue item (H7, session.updateQueue).</summary>
    Task<UpdateQueueResult> UpdateQueue(string sessionId, string itemId, QueueAction action, CancellationToken ct = default);
}

/// <summary>Default implementation over <see cref="WpfApiClient"/>.</summary>
public sealed class SessionService : ISessionService
{
    private readonly WpfApiClient _client;

    public SessionService(WpfApiClient client) => _client = client;

    public Task<SessionListResponse> List(string? workspaceId = null, CancellationToken ct = default)
    {
        return _client.Call<SessionListResponse>(
            RpcMethods.SessionList,
            workspaceId is null ? null : new { workspaceId },
            ct);
    }

    public Task<WorkspaceListResponse> ListWorkspaces(CancellationToken ct = default)
    {
        return _client.Call<WorkspaceListResponse>(RpcMethods.WorkspaceList, new { }, ct);
    }

    public Task<SettingsDescribeResult> DescribeSettings(CancellationToken ct = default)
    {
        return _client.Call<SettingsDescribeResult>(RpcMethods.SettingsDescribe, new { }, ct);
    }

    public Task<CredentialsDescribeResult> DescribeCredentials(IReadOnlyList<string> refs, CancellationToken ct = default)
    {
        return _client.Call<CredentialsDescribeResult>(RpcMethods.CredentialsDescribe, new { refs }, ct);
    }

    public Task<CredentialsMutateResult> SetCredential(string ref_, string value, CancellationToken ct = default)
    {
        return _client.Call<CredentialsMutateResult>(
            RpcMethods.CredentialsSet,
            new Dictionary<string, object> { ["ref"] = ref_, ["value"] = value },
            ct);
    }

    public Task<CredentialsMutateResult> UnsetCredential(string ref_, CancellationToken ct = default)
    {
        return _client.Call<CredentialsMutateResult>(
            RpcMethods.CredentialsUnset,
            new Dictionary<string, object> { ["ref"] = ref_ },
            ct);
    }

    public Task<SettingsWriteResult> MutateSettings(
        string ns,
        IReadOnlyList<SettingsPathOp> ops,
        long? expectedRevision,
        CancellationToken ct = default)
    {
        return _client.Call<SettingsWriteResult>(
            RpcMethods.SettingsMutate,
            new { ns, ops, expectedRevision },
            ct);
    }

    public Task<DiscoverModelsResult> DiscoverModels(string settingsNs, string provider, string? baseUrl, string? api, CancellationToken ct = default)
    {
        // Host schema (llm.schema.ts llmDiscoverModelsRequestSchema): settingsNs is required;
        // provider / baseURL / api / apiKey are optional STRINGS. Two prior bugs made every
        // probe fail with "Invalid payload for llm.discoverModels":
        //   1. The URL field is "baseURL" (capital URL) — we sent "baseUrl", which zod strips
        //      as an unknown key, so the base URL silently never reached the host.
        //   2. Optional fields must be ABSENT, not null: zod's `.optional()` accepts undefined
        //      but REJECTS an explicit null ("Expected string, received null"). The anonymous
        //      object serialized "baseUrl": null / "api": null on every call.
        // Build the payload as a dictionary containing only what is actually set (same pattern
        // as GetHistory) so absent fields are absent on the wire.
        var payload = new Dictionary<string, object> { ["settingsNs"] = settingsNs };
        if (!string.IsNullOrWhiteSpace(provider)) payload["provider"] = provider;
        if (!string.IsNullOrWhiteSpace(baseUrl)) payload["baseURL"] = baseUrl;
        if (!string.IsNullOrWhiteSpace(api)) payload["api"] = api;
        return _client.Call<DiscoverModelsResult>(
            RpcMethods.LlmDiscoverModels,
            payload,
            ct);
    }

    public Task<SessionHistoryPage> GetHistory(string sessionId, int? maxMessages = null, long? beforeSeq = null, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object> { ["sessionId"] = sessionId };
        if (maxMessages is not null) payload["maxMessages"] = maxMessages;
        if (beforeSeq is not null) payload["beforeSeq"] = beforeSeq;
        return _client.Call<SessionHistoryPage>(RpcMethods.SessionHistory, payload, ct);
    }

    public Task<SessionCreated> Create(string? workspaceId = null, string? cwd = null, CancellationToken ct = default)
    {
        // Build the payload conditionally: omit null fields so the wire JSON only carries
        // what the caller provided. `session.create` accepts workspaceId or cwd (not both,
        // and not neither) — host validates via `sessionCreateRequestSchema.refine`.
        object payload = (workspaceId, cwd) switch
        {
            (not null, null) => new { workspaceId },
            (null, not null) => new { cwd },
            (not null, not null) => new { workspaceId, cwd },
            _ => new { },
        };
        return _client.Call<SessionCreated>(RpcMethods.SessionCreate, payload, ct);
    }

    public Task<SessionPromptResult> Prompt(string sessionId, IReadOnlyList<PromptPart> content, string mode = "queue", CancellationToken ct = default)
    {
        return _client.Call<SessionPromptResult>(
            RpcMethods.SessionPrompt,
            new
            {
                sessionId,
                mode,
                content,
                clientTimeZone = TimeZoneSampler.Sample(),
            },
            ct);
    }

    public Task<SessionSearchResult> Search(string query, CancellationToken ct = default)
    {
        return _client.Call<SessionSearchResult>(RpcMethods.SessionSearch, new { query }, ct);
    }

    public Task<SessionForkResult> Fork(string sessionId, long? atSeq = null, CancellationToken ct = default)
    {
        return _client.Call<SessionForkResult>(
            RpcMethods.SessionFork,
            atSeq is null ? new { sessionId } : new { sessionId, atSeq },
            ct);
    }

    public Task<PickDirectoryResult> PickDirectory(CancellationToken ct = default)
    {
        return _client.Call<PickDirectoryResult>(RpcMethods.HostPickDirectory, new { }, ct);
    }

    public Task<WorkspaceCreateResult> CreateWorkspace(string path, CancellationToken ct = default)
    {
        return _client.Call<WorkspaceCreateResult>(RpcMethods.WorkspaceCreate, new { path }, ct);
    }

    public Task<WorkspaceDeleteResult> DeleteWorkspace(string workspaceId, CancellationToken ct = default)
    {
        return _client.Call<WorkspaceDeleteResult>(RpcMethods.WorkspaceDelete, new { workspaceId }, ct);
    }

    public Task<WorkspaceView> InsertWorkspaceBefore(string workspaceId, string? beforeId, CancellationToken ct = default)
    {
        return _client.Call<WorkspaceView>(RpcMethods.WorkspaceInsertBefore, new { workspaceId, beforeId }, ct);
    }

    public Task<SessionSummary> InsertSessionBefore(string workspaceId, string sessionId, string? beforeId, CancellationToken ct = default)
    {
        return _client.Call<SessionSummary>(RpcMethods.WorkspaceInsertSessionBefore, new { workspaceId, sessionId, beforeId }, ct);
    }

    public Task<SkillListResult> ListSkills(string sessionId, CancellationToken ct = default)
    {
        return _client.Call<SkillListResult>(RpcMethods.SkillList, new { sessionId }, ct);
    }

    public Task<AgentPresetListResult> ListAgentPresets(CancellationToken ct = default)
    {
        return _client.Call<AgentPresetListResult>(RpcMethods.AgentPresetList, new { }, ct);
    }

    public Task<UpdateQueueResult> UpdateQueue(string sessionId, string itemId, QueueAction action, CancellationToken ct = default)
    {
        return _client.Call<UpdateQueueResult>(
            RpcMethods.SessionUpdateQueue,
            new { sessionId, itemId, action },
            ct);
    }

    public Task<SessionRenamed> Rename(string sessionId, string title, CancellationToken ct = default)
    {
        return _client.Call<SessionRenamed>(
            RpcMethods.SessionRename,
            new { sessionId, title },
            ct);
    }

    public Task<ModelsResult> GetModels(string sessionId, CancellationToken ct = default)
    {
        return _client.Call<ModelsResult>(
            RpcMethods.SessionModels,
            new { sessionId },
            ct);
    }

    public Task<SelectModelResult> SelectModel(string sessionId, string provider, string model, CancellationToken ct = default)
    {
        return _client.Call<SelectModelResult>(
            RpcMethods.SessionSelectModel,
            new { sessionId, provider, model },
            ct);
    }

    public Task<SubagentCatalog> ListSubagents(string parentSessionId, CancellationToken ct = default)
    {
        return _client.Call<SubagentCatalog>(
            RpcMethods.SubagentList,
            new { parentSessionId },
            ct);
    }

    public Task<SubagentHistory> GetSubagentHistory(string parentSessionId, string childSessionId, string mode, CancellationToken ct = default)
    {
        return _client.Call<SubagentHistory>(
            RpcMethods.SubagentHistory,
            new
            {
                parentSessionId,
                childSessionId,
                mode,
            },
            ct);
    }

    public Task<SubagentInterruptReceipt> InterruptSubagent(string parentSessionId, string childSessionId, string mode, CancellationToken ct = default)
    {
        return _client.Call<SubagentInterruptReceipt>(
            RpcMethods.SubagentInterrupt,
            new { parentSessionId, childSessionId, mode },
            ct);
    }

    public Task<SubagentPromptReceipt> PromptSubagent(string parentSessionId, string childSessionId, string mode, IReadOnlyList<PromptPart> content, CancellationToken ct = default)
    {
        return _client.Call<SubagentPromptReceipt>(
            RpcMethods.SubagentPrompt,
            new
            {
                parentSessionId,
                childSessionId,
                mode,
                content,
                clientTimeZone = TimeZoneSampler.Sample(),
            },
            ct);
    }

    public Task<OpenPathResult> OpenPath(string path, CancellationToken ct = default)
    {
        return _client.Call<OpenPathResult>(RpcMethods.HostOpenPath, new { path }, ct);
    }

    public Task<Dsh.Contract.Methods.MessageFeedbackItem> PutMessageFeedback(string sessionId, string messageId, Dsh.Contract.Methods.MessageFeedbackRating rating, string? note = null, int? ifVersion = null, CancellationToken ct = default)
    {
        return _client.Call<Dsh.Contract.Methods.MessageFeedbackItem>(
            RpcMethods.MessageFeedbackPut,
            new { sessionId, messageId, rating, note, ifVersion },
            ct);
    }

    public Task<Dsh.Contract.Methods.MessageFeedbackListValue> ListMessageFeedback(string sessionId, CancellationToken ct = default)
    {
        return _client.Call<Dsh.Contract.Methods.MessageFeedbackListValue>(
            RpcMethods.MessageFeedbackList,
            new { sessionId },
            ct);
    }

    public Task<ArchiveSessionResult> ArchiveSession(string sessionId, CancellationToken ct = default)
    {
        return _client.Call<ArchiveSessionResult>(RpcMethods.WorkspaceArchiveSession, new { sessionId }, ct);
    }
}
