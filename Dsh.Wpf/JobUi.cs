using Dsh.App.Services;
using Dsh.Contract.Methods;
using Loc = Dsh.App.Services.Localization;

namespace Dsh.Wpf;

/// <summary>
/// UI wrapper around a <see cref="JobInfo"/> (P1-12) that adds a localized phase label derived
/// from the wire <c>Status</c>, so the jobs panel reads naturally (Queued/Running/Completed/Failed…).
/// The cancel/progress ring ask is contract-dependent (no <c>job.cancel</c> RPC or progress
/// field in the current host contract) and is intentionally omitted until the contract grows.
/// </summary>
public sealed class JobUi
{
    public JobUi(JobInfo job)
    {
        Info = job;
    }

    public JobInfo Info { get; }

    public string Id => Info.Id;
    public string Kind => Info.Kind;
    public string Label => Info.Label;
    public string Status => Info.Status;
    public string? Detail => Info.Detail;

    /// <summary>Human-readable phase for the job's lifecycle status (localized).</summary>
    public string StatusKindText => Status switch
    {
        "queued" => Loc.Get("Job.Queued"),
        "running" => Loc.Get("Job.Running"),
        "stopping" => Loc.Get("Job.Stopping"),
        "completed" => Loc.Get("Job.Completed"),
        "failed" => Loc.Get("Job.Failed"),
        "cancelled" => Loc.Get("Job.Cancelled"),
        _ => Status,
    };
}