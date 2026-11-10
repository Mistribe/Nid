using Microsoft.EntityFrameworkCore;
using Nid.Platform.Admin;
using Nid.Platform.Infrastructure.Persistence;
using NodaTime;

namespace Nid.Platform.Infrastructure.Admin;

/// <summary><see cref="IJobRunStore"/> on <see cref="PlatformDbContext"/>; every write saves at once (a run is not part of a unit of work).</summary>
public sealed class JobRunStore(PlatformDbContext db) : IJobRunStore
{
    public async Task StartAsync(Guid id, string jobKey, string trigger, DateTimeOffset startedAt, CancellationToken ct)
    {
        db.JobRuns.Add(new JobRunRow { Id = id, JobKey = jobKey, Trigger = trigger, StartedAt = Instant.FromDateTimeOffset(startedAt), Outcome = JobOutcomes.Running });
        await db.SaveChangesAsync(ct);
    }

    public async Task FinishAsync(Guid id, string outcome, string? summary, bool hasLogs, DateTimeOffset finishedAt, CancellationToken ct)
        => await db.JobRuns.Where(r => r.Id == id).ExecuteUpdateAsync(u => u
            .SetProperty(r => r.Outcome, outcome)
            .SetProperty(r => r.Summary, summary)
            .SetProperty(r => r.HasLogs, hasLogs)
            .SetProperty(r => r.FinishedAt, Instant.FromDateTimeOffset(finishedAt)), ct);

    public async Task<IReadOnlyDictionary<string, JobRunView>> LastRunsAsync(CancellationToken ct)
    {
        var rows = await db.JobRuns.AsNoTracking()
            .GroupBy(r => r.JobKey)
            .Select(g => g.OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id).First())
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.JobKey, View);
    }

    public async Task<IReadOnlyList<JobRunView>> ListAsync(string jobKey, int limit, CancellationToken ct)
        => (await Newest(jobKey).Take(limit).ToListAsync(ct)).Select(View).ToList();

    public async Task<(IReadOnlyList<JobRunView> Items, int Total)> PageAsync(string jobKey, int skip, int take, CancellationToken ct)
    {
        var rows = await Newest(jobKey).Skip(skip).Take(take).ToListAsync(ct);
        return (rows.Select(View).ToList(), await db.JobRuns.CountAsync(r => r.JobKey == jobKey, ct));
    }

    public async Task<(JobRunView Run, bool HasLogs)?> FindAsync(string jobKey, Guid id, CancellationToken ct)
    {
        var row = await db.JobRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.JobKey == jobKey, ct);
        return row is null ? null : (View(row), row.HasLogs);
    }

    public async Task<IReadOnlyList<Guid>> PurgeAsync(string jobKey, int keep, CancellationToken ct)
    {
        var ids = await Newest(jobKey).Skip(keep).Select(r => r.Id).ToListAsync(ct);
        if (ids.Count > 0)
            await db.JobRuns.Where(r => ids.Contains(r.Id)).ExecuteDeleteAsync(ct);
        return ids;
    }

    public Task<int> CloseStaleAsync(CancellationToken ct)
        => db.JobRuns.Where(r => r.Outcome == JobOutcomes.Running).ExecuteUpdateAsync(u => u
            .SetProperty(r => r.Outcome, JobOutcomes.Interrupted)
            .SetProperty(r => r.FinishedAt, r => r.StartedAt), ct);

    private IQueryable<JobRunRow> Newest(string jobKey)
        => db.JobRuns.AsNoTracking().Where(r => r.JobKey == jobKey).OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id);

    private static JobRunView View(JobRunRow r) => new(
        r.Id, r.Trigger, r.StartedAt.ToDateTimeOffset(), r.FinishedAt?.ToDateTimeOffset(),
        r.FinishedAt is { } end ? (long)(end - r.StartedAt).TotalMilliseconds : null, r.Outcome, r.Summary);
}
