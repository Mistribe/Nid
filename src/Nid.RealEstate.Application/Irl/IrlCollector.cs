using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using Duration = NodaTime.Duration;

namespace Nid.RealEstate.Application.Irl;

/// <summary>
/// One collection of the IRL series (rental spec §3.1), journaled as <c>PriceCollector</c> journals its runs: whatever goes wrong
/// ends in the journal as a failed run with nothing stored. A stored <c>insee</c> value is never touched; a <c>manual</c> one of
/// the same quarter is replaced; a quarter outside the plausibility band is rejected and named in the run's message.
/// </summary>
public sealed class IrlCollector(
    IIrlSourceClient source, IIrlStore store, IIrlRunLog runs, IHouseholdSettings settings, IHouseholdCalendar calendar, IClock clock)
{
    public const int RetentionDays = 400;
    public const string Insee = "insee", Manual = "manual";

    public async Task<Result<IrlRunView>> CollectAsync(CancellationToken ct)
    {
        if (!(await settings.ReadAsync(ct)).IrlEnabled)
            return Result.Failure<IrlRunView>(new Failure(IrlCodes.Disabled, "IRL collection is turned off for this household.", null));
        var run = await runs.TryStartAsync(ct);
        if (run is null)
            return Result.Failure<IrlRunView>(new Failure(IrlCodes.SyncRunning, "An IRL collection is already running.", null));

        int added = 0, replaced = 0, rejected = 0;
        string? message = null;
        var failed = false;
        var interrupted = false;
        try
        {
            var fetched = await source.FetchAsync(ct);
            if (fetched.TryGetFailure(out var failure))
                (failed, message) = (true, failure!.Message);
            else
            {
                var observations = IrlReader.Read(fetched.TryGetValue(out var body) ? body : []);
                (added, replaced, var rejectedQuarters) = await StoreAsync(observations, await calendar.TodayAsync(ct), ct);
                rejected = rejectedQuarters.Count;
                message = rejected == 0 ? null : "Rejected as implausible: " + string.Join(", ", rejectedQuarters) + ".";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            (failed, interrupted, message) = (true, true, "The run was interrupted before it finished (host shutting down).");
        }
        catch (FormatException)
        {
            (failed, message) = (true, "The INSEE answer is not a readable IRL series.");
        }
        catch (Exception e)
        {
            (failed, message) = (true, $"The run stopped on {e.GetType().Name}.");
        }

        var finished = run with
        {
            FinishedAt = clock.GetCurrentInstant().ToDateTimeOffset(),
            Status = failed ? "failed" : "succeeded",
            Added = added,
            Replaced = replaced,
            Rejected = rejected,
            Message = message,
        };
        await runs.FinishAsync(finished, CancellationToken.None); // a cancelled run is still closed
        if (interrupted)
            ct.ThrowIfCancellationRequested();
        await runs.PurgeBeforeAsync(clock.GetCurrentInstant() - Duration.FromDays(RetentionDays), CancellationToken.None);
        return Result.Success(finished);
    }

    private async Task<(int Added, int Replaced, List<IrlQuarter> Rejected)> StoreAsync(
        IReadOnlyList<(IrlQuarter Quarter, decimal Value, LocalDate? PublishedOn)> observations, LocalDate today, CancellationToken ct)
    {
        var stored = (await store.ListAsync(ct)).ToDictionary(v => v.Quarter);
        var batch = observations.GroupBy(o => o.Quarter).ToDictionary(g => g.Key, g => g.Last().Value);
        int added = 0, replaced = 0;
        List<IrlQuarter> rejected = [];

        foreach (var (quarter, value, publishedOn) in observations.OrderBy(o => o.Quarter.Year).ThenBy(o => o.Quarter.Quarter))
        {
            var existing = stored.GetValueOrDefault(quarter);
            if (existing?.Source == Insee)
                continue; // INSEE's first value is the one the rents were revised with, even if INSEE revises it later
            var lastYear = quarter with { Year = quarter.Year - 1 };
            decimal? reference = batch.TryGetValue(lastYear, out var inBatch) ? inBatch : stored.TryGetValue(lastYear, out var inStore) ? inStore.Value : null;
            if (!IrlPlausibility.Plausible(value, reference))
            {
                rejected.Add(quarter);
                continue;
            }
            await store.UpsertAsync(new IrlValue(quarter, value, publishedOn, today, Insee), ct);
            if (existing is null)
                added++;
            else
                replaced++;
        }
        return (added, replaced, rejected);
    }
}
