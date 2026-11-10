using Microsoft.EntityFrameworkCore;
using Nid.Investments.Contracts;
using NodaTime;
using Npgsql;
using Duration = NodaTime.Duration;

namespace Nid.Investments.Infrastructure.Persistence;

public sealed class PriceRunLog(InvestmentsDbContext db, IClock clock) : IPriceRunLog
{
    private const string UniqueViolation = "23505";

    private static readonly Duration AbandonedAfter = Duration.FromHours(1);

    public async Task<PriceRun?> TryStartAsync(CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var stale = now - AbandonedAfter;
        await db.PriceRuns
            .Where(r => r.Status == "running" && r.StartedAt < stale)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, "failed")
                .SetProperty(r => r.FinishedAt, (Instant?)now)
                .SetProperty(r => r.FailureCode, PriceCodes.RunAbandoned)
                .SetProperty(r => r.FailureMessage, "The run did not finish within an hour."), ct);

        var row = new PriceRunRow { Id = Guid.CreateVersion7(), StartedAt = now, Status = "running" };
        db.PriceRuns.Add(row);
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
        return ToRun(row);
    }

    public async Task FinishAsync(PriceRun run, CancellationToken ct)
        => await db.PriceRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, Status(run.Status))
            .SetProperty(r => r.FinishedAt, run.FinishedAt)
            .SetProperty(r => r.Queried, run.Queried)
            .SetProperty(r => r.Accepted, run.Accepted)
            .SetProperty(r => r.Rejected, run.Rejected)
            .SetProperty(r => r.NotFound, run.NotFound)
            .SetProperty(r => r.SourceErrors, run.SourceErrors)
            .SetProperty(r => r.FailureCode, run.FailureCode)
            .SetProperty(r => r.FailureMessage, run.FailureMessage), ct);

    public Task<bool> IsRunningAsync(CancellationToken ct) => db.PriceRuns.AnyAsync(r => r.Status == "running", ct);

    public async Task<IReadOnlyList<PriceRun>> RecentAsync(int count, CancellationToken ct)
        => (await db.PriceRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).Take(count).ToListAsync(ct)).Select(ToRun).ToList();

    public async Task<(IReadOnlyList<PriceRun> Items, int Total)> PageAsync(int skip, int take, CancellationToken ct)
    {
        var rows = await db.PriceRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id)
            .Skip(skip).Take(take).ToListAsync(ct);
        return (rows.Select(ToRun).ToList(), await db.PriceRuns.CountAsync(ct));
    }

    public async Task<int> PurgeBeforeAsync(Instant before, CancellationToken ct)
        => await db.PriceRuns.Where(r => r.StartedAt < before && r.Status != "running").ExecuteDeleteAsync(ct);

    private static string Status(PriceRunStatus status) => status switch
    {
        PriceRunStatus.Running => "running",
        PriceRunStatus.Succeeded => "succeeded",
        _ => "failed",
    };

    private static PriceRun ToRun(PriceRunRow row)
        => new(row.Id, row.StartedAt, row.FinishedAt,
            row.Status switch { "running" => PriceRunStatus.Running, "succeeded" => PriceRunStatus.Succeeded, _ => PriceRunStatus.Failed },
            row.Queried, row.Accepted, row.Rejected, row.NotFound, row.SourceErrors, row.FailureCode, row.FailureMessage);
}
