using Nid.Platform.Admin;
using Quartz;

namespace Nid.Host.Admin;

/// <summary>Jobs disabled on this instance (<c>Jobs:Enabled=false</c>): nothing is scheduled.</summary>
public sealed class NoJobStatusReader : IJobStatusReader
{
    public Task<IReadOnlyList<JobSchedule>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<JobSchedule>>([]);
}

/// <summary>
/// Reads the scheduler: each job's cron (its own schedule) and next fire time. The last run comes from the runs journal, which
/// also knows the runs of a job fired only on demand.
/// </summary>
public sealed class QuartzJobStatusReader(ISchedulerFactory schedulers) : IJobStatusReader
{
    public async Task<IReadOnlyList<JobSchedule>> ListAsync(CancellationToken ct)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        var jobs = new List<JobSchedule>();
        foreach (var key in (await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), ct)).OrderBy(k => k.Name, StringComparer.Ordinal))
        {
            var triggers = await scheduler.GetTriggersOfJob(key, ct);
            jobs.Add(new JobSchedule(
                key.Name,
                triggers.OfType<ICronTrigger>().Select(t => t.CronExpressionString).FirstOrDefault(),
                // Quartz 4.1.1: a property on ITrigger, not a method.
                triggers.Select(t => t.NextFireTimeUtc).Min()));
        }

        return jobs;
    }
}

/// <summary><see cref="IJobRunLogs"/> over the runs' log folder.</summary>
public sealed class JobRunLogs(Nid.Host.Jobs.RunLogFolder folder) : IJobRunLogs
{
    public bool Available => folder.Available;
    public IReadOnlyList<string>? Read(string jobKey, Guid runId) => folder.Read(jobKey, runId);
}
