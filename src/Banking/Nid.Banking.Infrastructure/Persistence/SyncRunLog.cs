using Microsoft.EntityFrameworkCore;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using NodaTime;
using Npgsql;
using Duration = NodaTime.Duration;

namespace Nid.Banking.Infrastructure.Persistence;

public sealed class SyncRunLog(BankingDbContext db, IClock clock) : IBankSyncRunLog
{
    private const string UniqueViolation = "23505";

    private static readonly Duration AbandonedAfter = Duration.FromHours(1);

    public async Task<BankSyncRunView?> TryStartAsync(string trigger, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var stale = now - AbandonedAfter;
        await db.SyncRuns
            .Where(r => r.Outcome == "running" && r.StartedAt < stale)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Outcome, "failed")
                .SetProperty(r => r.FinishedAt, (Instant?)now)
                .SetProperty(r => r.FailureCode, BankingCodes.RunAbandoned), ct);

        var row = new SyncRunRow { Id = Guid.CreateVersion7(), StartedAt = now, Trigger = trigger, Outcome = "running" };
        db.SyncRuns.Add(row);
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

    public async Task FinishAsync(BankSyncRunView run, CancellationToken ct)
    {
        Instant? finishedAt = run.FinishedAt is { } f ? Instant.FromDateTimeOffset(f) : null;
        await db.SyncRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Outcome, run.Outcome)
            .SetProperty(r => r.FinishedAt, finishedAt)
            .SetProperty(r => r.Connections, run.Connections)
            .SetProperty(r => r.Accounts, run.Accounts)
            .SetProperty(r => r.Balances, run.Balances)
            .SetProperty(r => r.Proposals, run.Proposals)
            .SetProperty(r => r.Written, run.Written)
            .SetProperty(r => r.Skipped, run.Skipped)
            .SetProperty(r => r.Transactions, run.Transactions)
            .SetProperty(r => r.FailureCode, run.FailureCode), ct);
    }

    public Task<bool> IsRunningAsync(CancellationToken ct) => db.SyncRuns.AnyAsync(r => r.Outcome == "running", ct);

    public async Task<IReadOnlyList<BankSyncRunView>> RecentAsync(int count, CancellationToken ct)
        => (await db.SyncRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).Take(count).ToListAsync(ct)).Select(ToView).ToList();

    public async Task<int> PurgeBeforeAsync(Instant before, CancellationToken ct)
        => await db.SyncRuns.Where(r => r.StartedAt < before && r.Outcome != "running").ExecuteDeleteAsync(ct);

    private static BankSyncRunView ToView(SyncRunRow r)
        => new(r.Id, r.StartedAt.ToDateTimeOffset(), r.FinishedAt?.ToDateTimeOffset(), r.Trigger, r.Outcome, r.Connections, r.Accounts, r.Balances,
            r.Proposals, r.Written, r.Skipped, r.FailureCode, r.Transactions);
}
