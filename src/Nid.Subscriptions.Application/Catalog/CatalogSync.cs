using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain.Catalog;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using Duration = NodaTime.Duration;

namespace Nid.Subscriptions.Application.Catalog;

/// <summary>
/// One sync of the pinned commit, journaled (design §3.6, §4). Whatever goes wrong ends in the journal as a failed run
/// and writes nothing. The one exception that leaves: a cancellation of the caller's token (the host shutting down),
/// after the run is closed as <see cref="CatalogCodes.RunInterrupted"/> (1C2a design §10).
/// After the merge commits, the icon pass runs (1C2c design §5); an icon failure is in the report, never a failed run,
/// and a cancellation during the pass closes the run as interrupted like any other.
/// </summary>
public sealed class CatalogSync(
    ICatalogSourceClient source, IProviderStore providers, ICatalogRunLog runs, IUnitOfWork unitOfWork,
    CatalogIconPass icons, CatalogOptions options, IClock clock)
{
    public async Task<Result<CatalogSyncRun>> RunAsync(CancellationToken ct)
    {
        var run = await runs.TryStartAsync(options.Commit, ct);
        if (run is null)
            return Result.Failure<CatalogSyncRun>(new Failure(CatalogCodes.SyncRunning, "A catalog sync is already running.", null));

        Failure? failure;
        CatalogReport? report = null;
        IReadOnlyDictionary<string, IReadOnlyList<ItemId>> usedBy = CatalogSyncRun.NoUsage;
        var interrupted = false;
        try
        {
            (failure, report, usedBy) = await SyncAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            interrupted = true;
            failure = new Failure(CatalogCodes.RunInterrupted, "The sync was interrupted before it finished (host shutting down).", null);
        }
        catch (Exception e)
        {
            failure = new Failure(CatalogCodes.SourceUnavailable, $"The sync stopped on {e.GetType().Name}.", null);
        }

        var finished = run with
        {
            FinishedAt = clock.GetCurrentInstant(),
            Status = failure is null ? CatalogRunStatus.Succeeded : CatalogRunStatus.Failed,
            FailureCode = failure?.Code,
            FailureMessage = failure?.Message,
            Report = failure is null ? report : null,
            UsedBy = failure is null ? usedBy : CatalogSyncRun.NoUsage,
        };
        await runs.FinishAsync(finished, CancellationToken.None); // a cancelled run is still closed
        if (interrupted)
            ct.ThrowIfCancellationRequested();
        await runs.PurgeBeforeAsync(clock.GetCurrentInstant() - Duration.FromDays(CatalogLimits.RunRetentionDays), CancellationToken.None);

        return Result.Success(finished);
    }

    private async Task<(Failure? Failure, CatalogReport? Report, IReadOnlyDictionary<string, IReadOnlyList<ItemId>> UsedBy)> SyncAsync(
        CancellationToken ct)
    {
        var fetched = await source.FetchAsync(options.Commit, ct);
        if (fetched.TryGetFailure(out var fetchFailure))
            return (fetchFailure, null, CatalogSyncRun.NoUsage);
        fetched.TryGetValue(out var files);

        var reading = CatalogReader.Read(files!.Labels, files.Providers);
        if (reading.TryGetFailure(out var readFailure))
            return (readFailure, null, CatalogSyncRun.NoUsage);
        reading.TryGetValue(out var parsed);

        var (report, usedBy) = await MergeAsync(parsed!, ct);
        // 1C2c design §5: after the commit, outside the lock; each icon links itself in a statement of its own.
        var iconReport = await icons.RunAsync(parsed!.IconsRefused, ct);
        return (null, report with { Icons = iconReport }, usedBy);
    }

    /// <summary>One transaction: the lock, the read under it and every write (design §4 "Merge", Review Focus 5 of 1C2b).</summary>
    private async Task<(CatalogReport Report, IReadOnlyDictionary<string, IReadOnlyList<ItemId>> UsedBy)> MergeAsync(
        CatalogReading reading, CancellationToken ct)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var plan = CatalogMerge.Plan(await providers.LockForSyncAsync(ct), reading, options.Commit);
        // Spec §3.6 "Judged as of the sync": who used each household entry, before the writes turn a replaced key into
        // a catalog one. The report is later filtered by these subscriptions, never by the key's use at reading time.
        var report = plan.Report;
        var usedBy = await providers.UsageAsync([.. report.AdoptedReplaced, .. report.AdoptedRemaining, .. report.Conflicts], ct);
        await providers.WriteCatalogAsync(plan, clock.GetCurrentInstant(), ct);
        await transaction.CommitAsync(ct);
        return (report, usedBy);
    }
}
