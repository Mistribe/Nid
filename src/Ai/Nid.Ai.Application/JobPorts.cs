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
    uint RowVersion)
{
    /// <summary>The adults whose <c>allow</c> the job needs to leave the premises (<see cref="JobRequest.Concerned"/>).</summary>
    public IReadOnlyList<Guid> Concerned { get; init; } = [];
}

public interface IJobStore
{
    /// <summary>Adds the job, or returns the one not finished with the same type and input (idempotence).</summary>
    Task<JobRecord> AddOrFindAsync(JobRecord job, CancellationToken ct);

    Task<JobRecord?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>#599: sets the class and the adults of the queued jobs of <paramref name="fileRef"/>, in one statement, so a
    /// dispatcher pass that read one before loses its claim on the row's version. True when one was queued.</summary>
    /// <remarks>ponytail: the input hash keeps the class it was submitted with; only a new submission of the same file under
    /// the new class (none today: the backfill skips a queued document) would not find it.</remarks>
    Task<bool> ReclassQueuedAsync(string fileRef, string dataClass, IReadOnlyList<Guid> concerned, CancellationToken ct);

    /// <summary>#600: deletes every job of <paramref name="fileRef"/>; a pass running one finds it gone and leaves it.</summary>
    Task DeleteOfFileAsync(string fileRef, CancellationToken ct);

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
    /// <summary><paramref name="offsite"/> false: an adult the job concerns has not allowed it off the premises, so only a
    /// local worker may take it (<see cref="JobCodes.OffsiteNotAllowed"/> when none can).</summary>
    Task<Route> RouteAsync(string type, int version, ModelCallKind kind, string dataClass, Guid? target, CancellationToken ct, bool offsite = true);
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
