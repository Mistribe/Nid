using Nid.Platform.Admin;
using Quartz;

namespace Nid.Host.Admin;

/// <summary>Jobs disabled on this instance (<c>Jobs:Enabled=false</c>): nothing is scheduled.</summary>
public sealed class NoJobStatusReader : IJobStatusReader
{
    public Task<IReadOnlyList<JobStatusView>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<JobStatusView>>([]);
}

/// <summary>
/// Reads the scheduler: last and next fire time from each job's triggers. A job fired only on demand (the backfill)
/// has no trigger left once it ran, so its last run is unknown here; the rate collection journal has it.
/// </summary>
public sealed class QuartzJobStatusReader(ISchedulerFactory schedulers) : IJobStatusReader
{
    public async Task<IReadOnlyList<JobStatusView>> ListAsync(CancellationToken ct)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        // Quartz 4.1.1: GetCurrentlyExecutingJobs was replaced by QueryFireInstances, which defaults to the
        // "Executing" state and pages its result; a couple of scheduled jobs never come close to a page.
        var running = (await scheduler.QueryFireInstances(new FireInstanceQuery(), ct)).Items
            .Select(f => f.JobKey)
            .Where(key => key is not null)
            .Select(key => key!)
            .ToHashSet();
        var views = new List<JobStatusView>();

        foreach (var key in (await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), ct)).OrderBy(k => k.Name, StringComparer.Ordinal))
        {
            var triggers = await scheduler.GetTriggersOfJob(key, ct);
            views.Add(new JobStatusView(
                key.Name,
                // Quartz 4.1.1: these are properties on ITrigger, not methods.
                triggers.Select(t => t.PreviousFireTimeUtc).Max(),
                triggers.Select(t => t.NextFireTimeUtc).Min(),
                running.Contains(key) ? "running" : "idle"));
        }

        return views;
    }
}
