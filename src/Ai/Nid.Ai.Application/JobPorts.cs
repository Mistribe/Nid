using Nid.Ai.Contracts;
using NodaTime;
using UnambitiousFx.Functional.Failures;

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

/// <summary>A job change the database refused for its content (not a concurrent change): the dispatcher fails that job
/// alone.</summary>
public sealed class JobWriteException(Exception inner) : Exception("A job change could not be stored.", inner);

/// <summary>A job or request refused, or a model call that failed: its code is one of <see cref="JobCodes"/>.</summary>
public static class ModelFailures
{
    public static Failure Of(string code) => new(code, $"Model call failed: {code}.", null);
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

    /// <summary>#531: how long the assistant's conversations are kept, 30 days until set.</summary>
    Task<int> ConversationRetentionDaysAsync(CancellationToken ct);

    Task SetConversationRetentionDaysAsync(int days, CancellationToken ct);
}
