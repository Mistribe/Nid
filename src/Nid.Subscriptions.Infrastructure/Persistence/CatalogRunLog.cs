using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Subscriptions.Contracts;
using NodaTime;
using Npgsql;
using Duration = NodaTime.Duration;

namespace Nid.Subscriptions.Infrastructure.Persistence;

/// <summary><c>subscriptions.catalog_sync_run</c>, with <c>core.fx_sync_run</c>'s rules (design §3.6).</summary>
public sealed class CatalogRunLog(SubscriptionsDbContext db, IClock clock) : ICatalogRunLog
{
    private static readonly Duration AbandonedAfter = Duration.FromHours(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CatalogSyncRun?> TryStartAsync(string commit, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var stale = now - AbandonedAfter;
        await db.CatalogSyncRuns
            .Where(r => r.Status == "running" && r.StartedAt < stale)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, "failed")
                .SetProperty(r => r.FinishedAt, (Instant?)now)
                .SetProperty(r => r.FailureCode, CatalogCodes.RunAbandoned)
                .SetProperty(r => r.FailureMessage, "The sync did not finish within an hour."), ct);

        var row = new CatalogSyncRunRow { Id = Guid.CreateVersion7(), StartedAt = now, Status = "running", Commit = commit };
        db.CatalogSyncRuns.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(row).State = EntityState.Detached;
            return null;
        }

        db.Entry(row).State = EntityState.Detached;
        return ToRun(row);
    }

    public async Task FinishAsync(CatalogSyncRun run, CancellationToken ct)
    {
        var report = run.Report is null ? null : JsonSerializer.Serialize(run.Report, Json);
        await db.CatalogSyncRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, Status(run.Status))
            .SetProperty(r => r.FinishedAt, run.FinishedAt)
            .SetProperty(r => r.FailureCode, run.FailureCode)
            .SetProperty(r => r.FailureMessage, run.FailureMessage)
            .SetProperty(r => r.Report, report), ct);
    }

    public Task<bool> IsRunningAsync(CancellationToken ct) => db.CatalogSyncRuns.AnyAsync(r => r.Status == "running", ct);

    public Task<bool> HasSucceededAsync(CancellationToken ct) => db.CatalogSyncRuns.AnyAsync(r => r.Status == "succeeded", ct);

    public async Task<IReadOnlyList<CatalogSyncRun>> RecentAsync(int count, CancellationToken ct)
        => (await db.CatalogSyncRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).Take(count).ToListAsync(ct))
            .Select(ToRun).ToList();

    public async Task<int> PurgeBeforeAsync(Instant before, CancellationToken ct)
    {
        // The latest succeeded run stays: the startup check (HasSucceededAsync) would otherwise sync again a year later.
        var keep = await db.CatalogSyncRuns.Where(r => r.Status == "succeeded")
            .OrderByDescending(r => r.StartedAt).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
        var purged = db.CatalogSyncRuns.Where(r => r.StartedAt < before && r.Status != "running");
        if (keep is { } id)
            purged = purged.Where(r => r.Id != id);
        return await purged.ExecuteDeleteAsync(ct);
    }

    private static string Status(CatalogRunStatus status) => status switch
    {
        CatalogRunStatus.Running => "running",
        CatalogRunStatus.Succeeded => "succeeded",
        _ => "failed",
    };

    private static CatalogSyncRun ToRun(CatalogSyncRunRow row)
        => new(row.Id, row.StartedAt, row.FinishedAt,
            row.Status switch { "running" => CatalogRunStatus.Running, "succeeded" => CatalogRunStatus.Succeeded, _ => CatalogRunStatus.Failed },
            row.Commit, row.FailureCode, row.FailureMessage,
            row.Report is null ? null : JsonSerializer.Deserialize<CatalogReport>(row.Report, Json));
}
