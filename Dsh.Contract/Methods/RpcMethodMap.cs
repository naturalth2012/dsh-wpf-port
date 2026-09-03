namespace Dsh.Contract.Methods;

/// <summary>
/// Method-name registry, mirroring the keys of <c>RpcMethodMap</c> in rpc-map.ts.
/// The map registers only client-request methods (respond is a client-response, so it is
/// absent); map keys are the wire path segments (<c>POST /api/session.list</c>).
/// This is the single source of truth the contract-consistency test asserts against.
/// </summary>
public static class RpcMethods
{
    // session domain
    public const string SessionList = "session.list";
    public const string SessionSearch = "session.search";
    public const string SessionCreate = "session.create";
    public const string SessionHistory = "session.history";
    public const string SessionModels = "session.models";
    public const string SessionSelectModel = "session.selectModel";
    public const string SessionRename = "session.rename";
    public const string SessionFork = "session.fork";
    public const string SessionPrompt = "session.prompt";
    public const string SessionAttachment = "session.attachment";
    public const string SessionUpdateQueue = "session.updateQueue";
    public const string SessionCancel = "session.cancel";

    // subagent domain (note: singular method keys, plural source file subagents.ts)
    public const string SubagentList = "subagent.list";
    public const string SubagentHistory = "subagent.history";
    public const string SubagentPrompt = "subagent.prompt";
    public const string SubagentInterrupt = "subagent.interrupt";

    // host domain
    public const string HostDescribe = "host.describe";
    public const string HostPickDirectory = "host.pickDirectory";
    public const string HostListDirectory = "host.listDirectory";
    public const string HostCreateDirectory = "host.createDirectory";
    public const string HostOpenPath = "host.openPath";

    // workspace domain
    public const string WorkspaceList = "workspace.list";
    public const string WorkspaceCreate = "workspace.create";
    public const string WorkspaceRename = "workspace.rename";
    public const string WorkspaceDelete = "workspace.delete";
    public const string WorkspaceInsertBefore = "workspace.insertBefore";
    public const string WorkspaceInsertSessionBefore = "workspace.insertSessionBefore";
    public const string WorkspaceArchiveSession = "workspace.archiveSession";

    // skill domain
    public const string SkillList = "skill.list";

    // agentPreset domain
    public const string AgentPresetList = "agentPreset.list";
    public const string AgentPresetSelect = "agentPreset.select";
    public const string AgentPresetRead = "agentPreset.read";
    public const string AgentPresetCopy = "agentPreset.copy";
    public const string AgentPresetOpenDocument = "agentPreset.openDocument";
    public const string AgentPresetRemove = "agentPreset.remove";

    // goal domain
    public const string GoalCreate = "goal.create";
    public const string GoalEdit = "goal.edit";
    public const string GoalPause = "goal.pause";
    public const string GoalResume = "goal.resume";
    public const string GoalComplete = "goal.complete";
    public const string GoalClear = "goal.clear";

    // settings domain
    public const string SettingsDescribe = "settings.describe";
    public const string SettingsOpenDocument = "settings.openDocument";
    public const string SettingsUpdate = "settings.update";
    public const string SettingsReplace = "settings.replace";
    public const string SettingsMutate = "settings.mutate";

    // credentials domain
    public const string CredentialsDescribe = "credentials.describe";
    public const string CredentialsSet = "credentials.set";
    public const string CredentialsUnset = "credentials.unset";

    // llm domain
    public const string LlmProviders = "llm.providers";
    public const string LlmModels = "llm.models";
    public const string LlmDiscoverModels = "llm.discoverModels";

    // messageFeedback domain (P2-4)
    public const string MessageFeedbackList = "messageFeedback.list";
    public const string MessageFeedbackPut = "messageFeedback.put";
    public const string MessageFeedbackDelete = "messageFeedback.delete";

    /// <summary>All 55 client-request method keys, in declaration order.</summary>
    public static readonly string[] All =
    {
        SessionList, SessionSearch, SessionCreate, SessionHistory, SessionModels,
        SessionSelectModel, SessionRename, SessionFork, SessionPrompt, SessionAttachment,
        SessionUpdateQueue, SessionCancel,
        SubagentList, SubagentHistory, SubagentPrompt, SubagentInterrupt,
        HostDescribe, HostPickDirectory, HostListDirectory, HostCreateDirectory, HostOpenPath,
        WorkspaceList, WorkspaceCreate, WorkspaceRename, WorkspaceDelete,
        WorkspaceInsertBefore, WorkspaceInsertSessionBefore, WorkspaceArchiveSession,
        SkillList,
        AgentPresetList, AgentPresetSelect, AgentPresetRead, AgentPresetCopy,
        AgentPresetOpenDocument, AgentPresetRemove,
        GoalCreate, GoalEdit, GoalPause, GoalResume, GoalComplete, GoalClear,
        SettingsDescribe, SettingsOpenDocument, SettingsUpdate, SettingsReplace, SettingsMutate,
        CredentialsDescribe, CredentialsSet, CredentialsUnset,
        LlmProviders, LlmModels, LlmDiscoverModels,
        MessageFeedbackList, MessageFeedbackPut, MessageFeedbackDelete,
    };
}
