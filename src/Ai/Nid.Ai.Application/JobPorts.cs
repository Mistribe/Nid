using System.Text.Json;
using Nid.Ai.Contracts;
using NodaTime;

namespace Nid.Ai.Application;

public enum JobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Dead,
}

/// <summary>A job as the dispatcher sees it (<c>ai.job</c>, <c>docs/architecture.md</c> §9.3). <see cref="ResponseSecret"/> is
/// the secret its result is bound to, kept (encrypted at rest) only while it runs.</summary>
public sealed record JobRecord(
    Guid Id,
    string Type,
    int Version,
    string Input,
    string InputHash,
    string? FileRef,
    string DataClass,
    Guid? TargetWorkerId,
    JobState Status,
    int Attempts,
    Instant RunAfter,
    Instant? NextPollAt,
    Instant? LeaseUntil,
    Guid? WorkerId,
    string? WorkerJobId,
    byte[]? ResponseSecret,
    string? Result,
    string? Error,
    string? WaitingReason,
    Instant CreatedAt,
    Instant? SentAt,
    Instant? FinishedAt,
    uint RowVersion);

public interface IJobStore
{
    /// <summary>Adds the job, or returns the one not finished with the same type and input (idempotence).</summary>
    Task<JobRecord> AddOrFindAsync(JobRecord job, CancellationToken ct);

    Task<JobRecord?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>Queued jobs whose time has come, oldest first.</summary>
    Task<IReadOnlyList<JobRecord>> DueAsync(Instant now, int limit, CancellationToken ct);

    /// <summary>Running jobs to poll now, or whose lease ran out.</summary>
    Task<IReadOnlyList<JobRecord>> RunningDueAsync(Instant now, int limit, CancellationToken ct);

    /// <summary>Deletes the jobs that succeeded before <paramref name="succeededBefore"/> and those that failed or died before
    /// <paramref name="failedBefore"/>, input and result with them; queued and running jobs are never touched. The count deleted.</summary>
    Task<int> PurgeFinishedAsync(Instant succeededBefore, Instant failedBefore, CancellationToken ct);

    /// <summary>One page of jobs, newest first, with the total and every type in the table.</summary>
    Task<(IReadOnlyList<JobRecord> Items, int Total, IReadOnlyList<string> Types)> PageAsync(
        string? status, string? type, int skip, int take, CancellationToken ct);

    /// <summary>Saves a change; false when another pass changed the job since it was read.</summary>
    Task<bool> TryUpdateAsync(JobRecord job, CancellationToken ct);
}

/// <summary>What a job carries: its type and version, its input as its contract reads it, an optional file reference
/// (<c>module:reference</c>, read by that module's <see cref="IJobFileSource"/> when the job is sent), its data class
/// (<c>docs/ai/README.md</c> §6.1) and, for an admin's test, the one worker it must run on.</summary>
public sealed record JobRequest(string Type, int Version, JsonElement Input, string DataClass, string? FileRef = null, Guid? TargetWorkerId = null);

/// <summary>A job's state for the people who look at it: never its input, its file or its secret.</summary>
public sealed record ModelJobView(
    Guid Id, string Type, int Version, string Status, int Attempts, string? WaitingReason, string? Error, Guid? WorkerId,
    Instant CreatedAt, Instant? FinishedAt, string? Result);

/// <summary>How a module sends work to the workers.</summary>
public interface IModelJobs
{
    /// <summary>Queues a job; the same type and input not finished yet give the same job.</summary>
    Task<Guid> SubmitAsync(JobRequest request, CancellationToken ct);

    Task<ModelJobView?> GetAsync(Guid id, CancellationToken ct);
}

/// <summary>Reads a job's file when it is sent: the module that owns it implements it (Documents for #167).</summary>
public interface IJobFileSource
{
    string Module { get; }

    Task<byte[]?> ReadAsync(string reference, CancellationToken ct);
}

/// <summary>A job or request type Nid knows: how its input and its output are read (<c>contracts/jobs</c>,
/// <c>contracts/requests</c>). A worker's output is stored or used only through this.</summary>
public interface IModelContract
{
    ModelCallKind Kind { get; }

    /// <summary>True only for a type that never carries household data (« Tester »'s echo): the class <c>none</c>, which
    /// any worker may receive, is accepted for it alone.</summary>
    bool AllowsNoData => false;

    string Type { get; }

    int Version { get; }

    bool ValidInput(ReadOnlySpan<byte> input);

    /// <summary>The output as Nid keeps it, or null when it does not follow the contract.</summary>
    string? ReadOutput(ReadOnlySpan<byte> output);
}

/// <summary>Where a job or request can go, best first; empty with the reason when nowhere.</summary>
public sealed record Route(IReadOnlyList<WorkerRecord> Workers, string? Reason);

public enum ModelCallKind
{
    Job,
    Request,
}

/// <summary>Where a job or request goes: eligible workers, best first.</summary>
public interface IWorkerRouter
{
    Task<Route> RouteAsync(string type, int version, ModelCallKind kind, string dataClass, Guid? target, CancellationToken ct);
}

/// <summary>A job or request refused, or a model call that failed (<see cref="JobCodes"/>).</summary>
/// <summary>A job change the database refused for its content (not a concurrent change): the dispatcher fails that job
/// alone.</summary>
public sealed class JobWriteException(Exception inner) : Exception("A job change could not be stored.", inner);

public sealed class ModelCallException(string code) : Exception($"Model call failed: {code}.")
{
    public string Code { get; } = code;
}

/// <summary>The household's AI settings (<c>ai.ai_settings</c>, <c>docs/ai/README.md</c> §13); for now, the opt-in for
/// remote workers, each change kept as a consent (<c>ai.consent</c>).</summary>
/// <summary>One use as the household set it: how far it may go and its model (none until an admin picks one).</summary>
public sealed record UseSetting(string Mode, string? Model);

public interface IAiSettings
{
    Task<bool> RemoteWorkersAllowedAsync(CancellationToken ct);

    /// <summary>Every use, each at its default (<see cref="UseModes.DefaultFor"/>, no model) until set.</summary>
    Task<IReadOnlyDictionary<string, UseSetting>> UsesAsync(CancellationToken ct);

    async Task<UseSetting> UseAsync(string use, CancellationToken ct)
        => (await UsesAsync(ct)).GetValueOrDefault(use) ?? new UseSetting(UseModes.DefaultFor(use), null);

    /// <summary>False when another adult changed the settings at the same moment: nothing was written.</summary>
    Task<bool> SetUseModeAsync(string use, string mode, CancellationToken ct);

    Task<bool> SetUseModelAsync(string use, string? model, CancellationToken ct);

    /// <summary>Records the adult's decision and applies it.</summary>
    /// <summary>False when another adult changed it at the same moment: nothing was written.</summary>
    Task<bool> SetRemoteWorkersAllowedAsync(bool allowed, Guid person, NodaTime.Instant at, CancellationToken ct);
}

public static class JobCodes
{
    public const string UnknownDataClass = "ai.unknown_data_class";
    public const string AllOffline = "ai.all_offline";
    public const string SettingsChanged = "ai.settings_changed";
    public const string UnknownType = "ai.unknown_type";
    public const string InvalidInput = "ai.invalid_input";
    public const string NoWorker = "ai.no_worker";
    public const string WorkerFailed = "ai.worker_failed";
    public const string ResultInvalid = "ai.result_invalid";
    public const string LeaseExpired = "ai.lease_expired";
    public const string TooLarge = "ai.too_large";
    public const string TimedOut = "ai.timed_out";

    /// <summary>#486: a use turned off, a use with no model set, a task a use may not run, no worker serving the use's model,
    /// a turn that ended without its usage, a use or mode that does not exist.</summary>
    public const string UseOff = "ai.use_off";
    public const string NoModel = "ai.no_model";
    public const string TaskNotAllowed = "ai.task_not_allowed";
    public const string ModelUnavailable = "ai.model_unavailable";
    public const string Cut = "ai.cut";
    public const string UnknownUse = "ai.unknown_use";
    public const string UnknownMode = "ai.unknown_mode";
    public const string RemoteNotAllowed = Domain.DataPolicy.RemoteNotAllowed;
    public const string NoSafeWorker = Domain.DataPolicy.NoSafeWorker;

    /// <summary>#487: data a cloud provider may not see, no enabled and eligible profile left, a provider that failed or refused.</summary>
    public const string CloudNotAllowed = "ai.cloud_not_allowed";
    public const string NoCloudProfile = "ai.no_cloud_profile";
    public const string CloudFailed = "ai.cloud_failed";
    public const string CloudRefused = "ai.cloud_refused";

    /// <summary>#488: a paid call over the month's ceiling (or with no rate to price it), and a ceiling out of range.</summary>
    public const string BudgetBlocked = "ai.budget_blocked";
    public const string InvalidBudget = "ai.invalid_budget";
}
