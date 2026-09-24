using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using NodaTime;
using NodaTime.Text;
using Npgsql;
using Duration = NodaTime.Duration;

namespace Nid.Core.Infrastructure.Persistence;

public sealed class FxRunLog(CoreDbContext db, IClock clock) : IFxRunLog
{
    private const string UniqueViolation = "23505";

    private static readonly Duration AbandonedAfter = Duration.FromHours(1);

    private sealed record RejectionJson(string OnDate, string Quote, decimal Rate, decimal? Previous, string Reason);

    public async Task<FxSyncRun?> TryStartAsync(FxRunKind kind, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var stale = now - AbandonedAfter;
        await db.FxSyncRuns
            .Where(r => r.Status == "running" && r.StartedAt < stale)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, "failed")
                .SetProperty(r => r.FinishedAt, (Instant?)now)
                .SetProperty(r => r.FailureCode, FxCodes.RunAbandoned)
                .SetProperty(r => r.FailureMessage, "The run did not finish within an hour."), ct);

        var row = new FxSyncRunRow
        {
            Id = Guid.CreateVersion7(),
            Kind = Kind(kind),
            StartedAt = now,
            Status = "running",
        };
        db.FxSyncRuns.Add(row);
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

    public async Task FinishAsync(FxSyncRun run, CancellationToken ct)
    {
        var rejected = JsonSerializer.Serialize(run.Rejected.Select(r =>
            new RejectionJson(LocalDatePattern.Iso.Format(r.OnDate), r.Quote, r.Rate, r.Previous, r.Reason)).ToList());
        await db.FxSyncRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, Status(run.Status))
            .SetProperty(r => r.FinishedAt, run.FinishedAt)
            .SetProperty(r => r.FilesRead, run.FilesRead)
            .SetProperty(r => r.RowsWritten, run.RowsWritten)
            .SetProperty(r => r.FailureCode, run.FailureCode)
            .SetProperty(r => r.FailureMessage, run.FailureMessage)
            .SetProperty(r => r.Rejected, rejected), ct);
    }

    public Task<bool> IsRunningAsync(CancellationToken ct) => db.FxSyncRuns.AnyAsync(r => r.Status == "running", ct);

    public Task<bool> HasSucceededAsync(FxRunKind kind, CancellationToken ct)
    {
        var wire = Kind(kind);
        return db.FxSyncRuns.AnyAsync(r => r.Kind == wire && r.Status == "succeeded", ct);
    }

    public async Task<IReadOnlyList<FxSyncRun>> RecentAsync(int count, CancellationToken ct)
        => (await db.FxSyncRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).Take(count).ToListAsync(ct))
            .Select(ToRun).ToList();

    public async Task<int> PurgeBeforeAsync(Instant before, CancellationToken ct)
    {
        // The latest succeeded backfill stays: the startup check (HasSucceededAsync) would otherwise re-run it.
        var keep = await db.FxSyncRuns.Where(r => r.Kind == "backfill" && r.Status == "succeeded")
            .OrderByDescending(r => r.StartedAt).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
        var purged = db.FxSyncRuns.Where(r => r.StartedAt < before && r.Status != "running");
        if (keep is { } id)
            purged = purged.Where(r => r.Id != id);
        return await purged.ExecuteDeleteAsync(ct);
    }

    private static string Kind(FxRunKind kind) => kind == FxRunKind.Daily ? "daily" : "backfill";

    private static string Status(FxRunStatus status) => status switch
    {
        FxRunStatus.Running => "running",
        FxRunStatus.Succeeded => "succeeded",
        _ => "failed",
    };

    private static FxSyncRun ToRun(FxSyncRunRow row)
        => new(row.Id,
            row.Kind == "daily" ? FxRunKind.Daily : FxRunKind.Backfill,
            row.StartedAt, row.FinishedAt,
            row.Status switch { "running" => FxRunStatus.Running, "succeeded" => FxRunStatus.Succeeded, _ => FxRunStatus.Failed },
            row.FilesRead, row.RowsWritten, row.FailureCode, row.FailureMessage,
            (JsonSerializer.Deserialize<List<RejectionJson>>(row.Rejected) ?? [])
                .Select(r => new FxRejection(LocalDatePattern.Iso.Parse(r.OnDate).Value, r.Quote, r.Rate, r.Previous, r.Reason))
                .ToList());
}
