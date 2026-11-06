using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using NodaTime;
using Npgsql;
using Duration = NodaTime.Duration;

namespace Nid.Core.Infrastructure.Persistence;

public sealed class CpiRunLog(CoreDbContext db, IClock clock) : ICpiRunLog
{
    private const string UniqueViolation = "23505";

    private static readonly Duration AbandonedAfter = Duration.FromHours(1);

    public async Task<CpiRunView?> TryStartAsync(CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var stale = now - AbandonedAfter;
        await db.CpiRuns
            .Where(r => r.Status == "running" && r.StartedAt < stale)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, "failed")
                .SetProperty(r => r.FinishedAt, (Instant?)now)
                .SetProperty(r => r.Message, "The run did not finish within an hour."), ct);

        var row = new CpiRunRow { Id = Guid.CreateVersion7(), StartedAt = now, Status = "running" };
        db.CpiRuns.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            db.Entry(row).State = EntityState.Detached;
            return null;
        }

        db.Entry(row).State = EntityState.Detached;
        return ToView(row);
    }

    public async Task FinishAsync(CpiRunView run, CancellationToken ct)
    {
        Instant? finishedAt = run.FinishedAt is { } f ? Instant.FromDateTimeOffset(f) : null;
        await db.CpiRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, run.Status)
            .SetProperty(r => r.FinishedAt, finishedAt)
            .SetProperty(r => r.Added, run.Added)
            .SetProperty(r => r.Replaced, run.Replaced)
            .SetProperty(r => r.Rejected, run.Rejected)
            .SetProperty(r => r.Message, run.Message), ct);
    }

    public Task<bool> IsRunningAsync(CancellationToken ct) => db.CpiRuns.AnyAsync(r => r.Status == "running", ct);

    public async Task<IReadOnlyList<CpiRunView>> RecentAsync(int count, CancellationToken ct)
        => (await db.CpiRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).Take(count).ToListAsync(ct)).Select(ToView).ToList();

    public async Task<int> PurgeBeforeAsync(Instant before, CancellationToken ct)
        => await db.CpiRuns.Where(r => r.StartedAt < before && r.Status != "running").ExecuteDeleteAsync(ct);

    private static CpiRunView ToView(CpiRunRow r) => new(r.Id, r.StartedAt.ToDateTimeOffset(), r.FinishedAt?.ToDateTimeOffset(), r.Status, r.Added, r.Replaced, r.Rejected, r.Message);
}
