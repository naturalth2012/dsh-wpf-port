using Dsh.App.Services;
using Dsh.Contract.Methods;
using Loc = Dsh.App.Services.Localization;

namespace Dsh.Wpf;

/// <summary>Stable status keys so XAML DataTriggers compare culture-invariant values.</summary>
public enum SubagentStatus
{
    Running,
    Paused,
    ReadOnly,
    Interrupted,
    Terminated,
    Idle,
}

/// <summary>Bindable subagent node for the subagent panel.</summary>
public sealed record SubagentNodeUi(
    string Id,
    string Label,
    string Mode,
    string Activity,
    bool HasChildren,
    bool IsDiagnostic,
    string ParentSessionId)
{
    /// <summary>True when the parent is available and the child is continuable (F3); otherwise read-only (F2/F4).</summary>
    public bool IsContinuable => !IsDiagnostic && Mode == "continuable";

    /// <summary>
    /// Culture-invariant status key (P1-11) driving the status dot color. XAML DataTriggers compare
    /// this value instead of the localized text, so the dot stays correct under any language.
    /// </summary>
    public SubagentStatus Status => IsDiagnostic ? SubagentStatus.ReadOnly
        : Activity switch
        {
            "running" => SubagentStatus.Running,
            "paused" => SubagentStatus.Paused,
            "terminated" => SubagentStatus.Terminated,
            "interrupted" => SubagentStatus.Interrupted,
            _ => SubagentStatus.Idle,
        };

    /// <summary>Localized status text shown next to the dot.</summary>
    public string StatusKindText => Loc.Get(Status switch
    {
        SubagentStatus.Running => "Subagent.Running",
        SubagentStatus.Paused => "Subagent.Paused",
        SubagentStatus.ReadOnly => "Subagent.ReadOnly",
        SubagentStatus.Interrupted => "Subagent.Interrupted",
        SubagentStatus.Terminated => "Subagent.Terminated",
        _ => "Subagent.Idle",
    });

    public static SubagentNodeUi From(SubagentEntry entry, string parentSessionId) =>
        entry switch
        {
            SubagentChild c => new SubagentNodeUi(
                Id: c.Id,
                Label: c.Label ?? "(无标题)",
                Mode: c.Mode,
                Activity: c.Activity,
                HasChildren: c.HasChildren,
                IsDiagnostic: false,
                ParentSessionId: parentSessionId),
            SubagentDiagnostic d => new SubagentNodeUi(
                Id: d.Id,
                Label: $"[诊断] {d.Reason}",
                Mode: "diagnostic",
                Activity: "inactive",
                HasChildren: false,
                IsDiagnostic: true,
                ParentSessionId: parentSessionId),
            _ => new SubagentNodeUi(entry.Id, entry.Id, "unknown", "inactive", false, true, parentSessionId),
        };
}
