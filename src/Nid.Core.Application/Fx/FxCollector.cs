using Nid.Core.Contracts;
using Nid.Core.Domain.Fx;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using Duration = NodaTime.Duration;

namespace Nid.Core.Application.Fx;

/// <summary>
/// One collection run, journaled (design §6.2). Whatever goes wrong ends in the journal as a failed run, and the next
/// run retries. The one exception that leaves: a cancellation of the caller's token, after the run is closed as
/// <see cref="FxCodes.RunInterrupted"/>. Days are fetched one at a time, in order, so a failure leaves every earlier
/// day written and the next run resumes after them.
/// </summary>
public sealed class FxCollector(IFxSourceClient sources, IFxRateStore rates, IFxRunLog runs, FxOptions options, IClock clock)
{
    public const int RunRetentionDays = 400;

    private sealed class Progress
    {
        public int FilesRead;
        public int RowsWritten;
        public List<FxRejection> Rejected { get; } = [];
    }

    private LocalDate Today => clock.GetCurrentInstant().InUtc().Date;

    public Task<Result<FxSyncRun>> CollectDailyAsync(CancellationToken ct) => RunAsync(FxRunKind.Daily, async (progress, token) =>
    {
        var stored = await rates.DatesFromAsync(FxRateSource.SubTrackerData, options.SubTrackerStart, token);
        var first = stored.Count == 0 ? options.SubTrackerStart : stored.Max().PlusDays(1);
        return await CollectSubTrackerAsync(Days(first, Today), progress, token);
    }, ct);

    public Task<Result<FxSyncRun>> BackfillAsync(CancellationToken ct) => RunAsync(FxRunKind.Backfill, async (progress, token) =>
    {
        // The ECB first: it only fills gaps, and SubTracker rates then have something to be checked against (§4.4).
        var ecb = await sources.EcbHistoryAsync(token);
        if (ecb.TryGetFailure(out var fetchFailure))
            return fetchFailure;
        ecb.TryGetValue(out var file);
        if (file!.Bytes is not { } zip) // the client maps a 404 to a failure: the history file must exist
            return new Failure(FxCodes.SourceUnavailable, "The ECB sent no history file.", null);

        progress.FilesRead++;
        var reading = EcbReader.Read(zip);
        if (reading.TryGetFailure(out var readFailure))
            return readFailure;
        reading.TryGetValue(out var history);
        progress.RowsWritten += await rates.WriteAsync(history!.Rates, token);
        progress.Rejected.AddRange(history.Rejected);

        var stored = await rates.DatesFromAsync(FxRateSource.SubTrackerData, options.SubTrackerStart, token);
        return await CollectSubTrackerAsync(Days(options.SubTrackerStart, Today).Where(d => !stored.Contains(d)), progress, token);
    }, ct);

    private async Task<Failure?> CollectSubTrackerAsync(IEnumerable<LocalDate> days, Progress progress, CancellationToken ct)
    {
        foreach (var day in days)
        {
            var fetched = await sources.SubTrackerDayAsync(day, ct);
            if (fetched.TryGetFailure(out var fetchFailure))
                return fetchFailure;
            fetched.TryGetValue(out var file);
            if (file!.Bytes is not { } json)
                continue; // no file that day: the "last known rate" rule covers it

            progress.FilesRead++;
            var reading = SubTrackerReader.Read(json, day);
            if (reading.TryGetFailure(out var readFailure))
                return readFailure;
            reading.TryGetValue(out var parsed);

            var previous = await rates.PreviousAsync(parsed!.Rates.Select(r => r.Quote).ToList(), day, ct);
            var checkedRates = FxPlausibility.Check(parsed.Rates, previous, options.MaxDailyChange);
            progress.RowsWritten += await rates.WriteAsync(checkedRates.Rates, ct);
            progress.Rejected.AddRange(parsed.Rejected);
            progress.Rejected.AddRange(checkedRates.Rejected);
        }

        return null;
    }

    private async Task<Result<FxSyncRun>> RunAsync(FxRunKind kind, Func<Progress, CancellationToken, Task<Failure?>> work, CancellationToken ct)
    {
        var run = await runs.TryStartAsync(kind, ct);
        if (run is null)
            return Result.Failure<FxSyncRun>(new Failure(FxCodes.SyncRunning, "A rate collection is already running.", null));

        var progress = new Progress();
        Failure? failure;
        var interrupted = false;
        try
        {
            failure = await work(progress, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            interrupted = true;
            failure = new Failure(FxCodes.RunInterrupted, "The run was interrupted before it finished (host shutting down).", null);
        }
        catch (Exception e)
        {
            failure = new Failure(FxCodes.SourceUnavailable, $"The run stopped on {e.GetType().Name}.", null);
        }

        var finished = run with
        {
            FinishedAt = clock.GetCurrentInstant(),
            Status = failure is null ? FxRunStatus.Succeeded : FxRunStatus.Failed,
            FilesRead = progress.FilesRead,
            RowsWritten = progress.RowsWritten,
            FailureCode = failure?.Code,
            FailureMessage = failure?.Message,
            Rejected = progress.Rejected,
        };
        await runs.FinishAsync(finished, CancellationToken.None); // a cancelled run is still closed
        if (interrupted)
            ct.ThrowIfCancellationRequested();
        if (kind == FxRunKind.Daily)
            await runs.PurgeBeforeAsync(clock.GetCurrentInstant() - Duration.FromDays(RunRetentionDays), CancellationToken.None);

        return Result.Success(finished);
    }

    private static IEnumerable<LocalDate> Days(LocalDate first, LocalDate last)
    {
        for (var day = first; day <= last; day = day.PlusDays(1))
            yield return day;
    }
}
