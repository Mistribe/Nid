using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Admin;

/// <summary>How a job run ended, or <c>running</c> while it runs (admin jobs design §2.1).</summary>
public static class JobOutcomes
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
    public const string Interrupted = "interrupted";
}

/// <summary>What fired a run: one of the job's own triggers, or a one-shot trigger (startup, « Synchroniser »).</summary>
public static class JobTriggers
{
    public const string Scheduled = "scheduled";
    public const string OnDemand = "on_demand";
}

/// <summary>One run; <see cref="DurationMs"/> is the server's, the browser computes nothing.</summary>
public sealed record JobRunView(Guid Id, string Trigger, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, long? DurationMs, string Outcome, string? Summary);

/// <summary>The runs journal (<c>platform.job_run</c>). Times come from Quartz, not from the clock: they are the run's real times.</summary>
public interface IJobRunStore
{
    Task StartAsync(Guid id, string jobKey, string trigger, DateTimeOffset startedAt, CancellationToken ct);
    Task FinishAsync(Guid id, string outcome, string? summary, bool hasLogs, DateTimeOffset finishedAt, CancellationToken ct);
    Task<IReadOnlyDictionary<string, JobRunView>> LastRunsAsync(CancellationToken ct);
    Task<IReadOnlyList<JobRunView>> ListAsync(string jobKey, int limit, CancellationToken ct);
    Task<(JobRunView Run, bool HasLogs)?> FindAsync(string jobKey, Guid id, CancellationToken ct);

    /// <summary>Deletes the job's runs beyond its <paramref name="keep"/> most recent; returns their ids.</summary>
    Task<IReadOnlyList<Guid>> PurgeAsync(string jobKey, int keep, CancellationToken ct);

    /// <summary>Closes the runs a dead process left <c>running</c> as <c>interrupted</c>.</summary>
    Task<int> CloseStaleAsync(CancellationToken ct);
}

/// <summary>A job as the scheduler knows it: <see cref="Cron"/> is its own cron trigger (UTC), null for a job fired on demand.</summary>
public sealed record JobSchedule(string Key, string? Cron, DateTimeOffset? NextRunAt);

/// <summary>A row of Administration › Tâches.</summary>
public sealed record JobView(string Key, string? Cron, DateTimeOffset? NextRunAt, JobRunView? LastRun);

public sealed record JobDetailView(string Key, string? Cron, DateTimeOffset? NextRunAt, IReadOnlyList<JobRunView> Runs);

/// <summary>
/// A run with its log lines: <see cref="Logs"/> is null when the run wrote none; <see cref="LogsAvailable"/> is false when file
/// logs are off on this instance, so the page can tell the two apart.
/// </summary>
public sealed record JobRunDetailView(
    Guid Id, string Trigger, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, long? DurationMs, string Outcome, string? Summary,
    IReadOnlyList<string>? Logs, bool LogsAvailable);

/// <summary>The scheduler's jobs; empty when jobs are disabled on this instance. Implemented by the host (Quartz).</summary>
public interface IJobStatusReader
{
    Task<IReadOnlyList<JobSchedule>> ListAsync(CancellationToken ct);
}

/// <summary>A run's log file, filtered when written (design §3.3). Implemented by the host, which knows <c>FileLogs:Path</c>.</summary>
public interface IJobRunLogs
{
    bool Available { get; }
    IReadOnlyList<string>? Read(string jobKey, Guid runId);
}

public sealed record ListJobsQuery : IQuery<IReadOnlyList<JobView>>, IAdminOnly;

public sealed record GetJobQuery(string Key) : IQuery<JobDetailView>, IAdminOnly;

public sealed record GetJobRunQuery(string Key, Guid RunId) : IQuery<JobRunDetailView>, IAdminOnly;

[RequestHandler<ListJobsQuery, IReadOnlyList<JobView>>]
public sealed class ListJobsHandler(IJobStatusReader jobs, IJobRunStore runs) : IRequestHandler<ListJobsQuery, IReadOnlyList<JobView>>
{
    public async ValueTask<Result<IReadOnlyList<JobView>>> HandleAsync(ListJobsQuery query, CancellationToken ct = default)
    {
        var schedules = await jobs.ListAsync(ct);
        if (schedules.Count == 0)
            return Result.Success<IReadOnlyList<JobView>>([]);
        var last = await runs.LastRunsAsync(ct);
        return Result.Success<IReadOnlyList<JobView>>(schedules
            .Select(j => new JobView(j.Key, j.Cron, j.NextRunAt, last.GetValueOrDefault(j.Key)))
            .ToList());
    }
}

[RequestHandler<GetJobQuery, JobDetailView>]
public sealed class GetJobHandler(IJobStatusReader jobs, IJobRunStore runs) : IRequestHandler<GetJobQuery, JobDetailView>
{
    public async ValueTask<Result<JobDetailView>> HandleAsync(GetJobQuery query, CancellationToken ct = default)
    {
        if ((await jobs.ListAsync(ct)).FirstOrDefault(j => j.Key == query.Key) is not { } job)
            return Result.Failure<JobDetailView>(new NotFoundFailure("job", query.Key));
        return Result.Success(new JobDetailView(job.Key, job.Cron, job.NextRunAt, await runs.ListAsync(job.Key, JobRunsKept.Count, ct)));
    }
}

[RequestHandler<GetJobRunQuery, JobRunDetailView>]
public sealed class GetJobRunHandler(IJobStatusReader jobs, IJobRunStore runs, IJobRunLogs logs) : IRequestHandler<GetJobRunQuery, JobRunDetailView>
{
    public async ValueTask<Result<JobRunDetailView>> HandleAsync(GetJobRunQuery query, CancellationToken ct = default)
    {
        // Only a key the scheduler knows reaches the log folder.
        if ((await jobs.ListAsync(ct)).All(j => j.Key != query.Key) || await runs.FindAsync(query.Key, query.RunId, ct) is not { } found)
            return Result.Failure<JobRunDetailView>(new NotFoundFailure("job_run", query.RunId.ToString()));
        var r = found.Run;
        return Result.Success(new JobRunDetailView(r.Id, r.Trigger, r.StartedAt, r.FinishedAt, r.DurationMs, r.Outcome, r.Summary,
            // Read whatever has_logs says: a run still running, or left by a dead process, has its lines on disk already.
            logs.Read(query.Key, r.Id), logs.Available));
    }
}

/// <summary>How many runs of each job are kept (design §1, decision 4).</summary>
public static class JobRunsKept
{
    public const int Count = 100;
}
