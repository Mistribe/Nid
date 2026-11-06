using Nid.Core.Contracts;
using Nid.Core.Domain.Inflation;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using Duration = NodaTime.Duration;

namespace Nid.Core.Application.Inflation;

/// <summary>
/// One collection of the CPI series (inflation spec), journaled as the IRL collector does: whatever goes wrong ends
/// in the journal as a failed run with nothing stored. INSEE's value always replaces the stored one of the same month, manual or
/// not (INSEE revises a month at most once, and a rebased series must land whole); a month outside the plausibility band against
/// the month before is rejected and named in the run's message.
/// </summary>
public sealed class CpiCollector(
    ICpiSourceClient source, ICpiStore store, ICpiRunLog runs, IHouseholdSettings settings, IHouseholdCalendar calendar, IClock clock)
{
    public const int RetentionDays = 400;
    public const string Insee = "insee", Manual = "manual";

    public async Task<Result<CpiRunView>> CollectAsync(CancellationToken ct)
    {
        if (!(await settings.ReadAsync(ct)).InflationEnabled)
            return Result.Failure<CpiRunView>(new Failure(InflationCodes.Disabled, "CPI collection is turned off for this household.", null));
        var run = await runs.TryStartAsync(ct);
        if (run is null)
            return Result.Failure<CpiRunView>(new Failure(InflationCodes.SyncRunning, "A CPI collection is already running.", null));

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
                var observations = CpiReader.Read(fetched.TryGetValue(out var body) ? body : []);
                (added, replaced, var rejectedMonths) = await StoreAsync(observations, await calendar.TodayAsync(ct), ct);
                rejected = rejectedMonths.Count;
                message = rejected == 0 ? null : "Rejected as implausible: " + string.Join(", ", rejectedMonths.Select(CpiViews.Month)) + ".";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            (failed, interrupted, message) = (true, true, "The run was interrupted before it finished (host shutting down).");
        }
        catch (FormatException)
        {
            (failed, message) = (true, "The INSEE answer is not a readable CPI series.");
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

    private async Task<(int Added, int Replaced, List<YearMonth> Rejected)> StoreAsync(
        IReadOnlyList<(YearMonth Month, decimal Value, LocalDate? PublishedOn)> observations, LocalDate today, CancellationToken ct)
    {
        var stored = (await store.ListAsync(ct)).ToDictionary(v => v.Month);
        var batch = observations.GroupBy(o => o.Month).ToDictionary(g => g.Key, g => g.Last().Value);
        int added = 0, replaced = 0;
        List<YearMonth> rejected = [];
        List<CpiValue> accepted = [];

        foreach (var (month, value, publishedOn) in observations.OrderBy(o => o.Month))
        {
            var existing = stored.GetValueOrDefault(month);
            if (existing is { Source: Insee } && existing.Value == value)
                continue; // nothing new, counted nowhere
            var before = month.PlusMonths(-1);
            decimal? reference = batch.TryGetValue(before, out var inBatch) ? inBatch : stored.TryGetValue(before, out var inStore) ? inStore.Value : null;
            if (!CpiPlausibility.Plausible(value, reference))
            {
                rejected.Add(month);
                continue;
            }
            accepted.Add(new CpiValue(month, value, publishedOn, today, Insee));
            if (existing is null)
                added++;
            else
                replaced++;
        }
        await store.UpsertManyAsync(accepted, ct); // one write: a failure mid-way must not leave a mixed-base series
        return (added, replaced, rejected);
    }
}
