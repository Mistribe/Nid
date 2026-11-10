using Nid.Core.Contracts;
using Nid.Income.Contracts;
using Nid.Income.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Income.Application;

/// <summary>
/// 2D1 spec §4: the monthly equivalent of the phases running today, over the reader's visible open sources.
/// A declared source with no running phase still puts an entry (0) in its currency's sum: the aggregation reads
/// « no income declared » as zero sources, so such a source gives 0, not « unknown ».
/// </summary>
public sealed class IncomeSourcesIncome(IVisibleItems visible, IItemRegistry items, ISourceStore sources, IPhaseStore phases) : IIncomeContribution
{
    public string Module => "income";

    public async Task<Result<IncomePart>> IncomeAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct)
    {
        var found = await sources.FindManyAsync((await visible.ForAsync(reader, scope, ct)).ToList(), ct);
        var bySource = (await phases.ForSourcesAsync(found.Select(s => s.ItemId).ToList(), ct)).ToLookup(p => p.Source);
        var accesses = await items.FindManyAsync(found.Select(s => s.ItemId).ToList(), ct); // batched reads spec §3
        var sums = new Dictionary<Currency, decimal>();
        var mine = new Dictionary<Currency, decimal>();
        var count = 0;
        foreach (var source in found)
        {
            if (!accesses.TryGetValue(source.ItemId, out var item) || item.ArchivedAt is not null)
                continue;
            count++;
            var currency = SourceCheck.CurrencyOf(item);
            var amount = IncomeMath.MonthlyEquivalent(bySource[source.ItemId], today);
            sums[currency] = sums.GetValueOrDefault(currency) + amount;
            mine[currency] = mine.GetValueOrDefault(currency) + amount * item.ShareOf(reader) / 10_000m;
        }
        return Result.Success(new IncomePart(count, sums, mine));
    }

    /// <summary>
    /// Projection spec §3.2, §5.1: <c>Monthly[k]</c> on the last day of month (this month + k), <c>Monthly[0]</c> on today, over the
    /// reader's visible open sources (as <see cref="IncomeAsync"/>). A phase ending without a successor is extended and its source
    /// reported by name; an ended source (<c>EndedOn</c>) stops.
    /// </summary>
    public async Task<Result<IncomeTimeline>> TimelineAsync(PersonId reader, VisibilityScope scope, LocalDate today, int months, IReadOnlyDictionary<PersonId, int> switches, CancellationToken ct)
    {
        var found = await sources.FindManyAsync((await visible.ForAsync(reader, scope, ct)).ToList(), ct);
        var bySource = (await phases.ForSourcesAsync(found.Select(s => s.ItemId).ToList(), ct)).ToLookup(p => p.Source);
        var accesses = await items.FindManyAsync(found.Select(s => s.ItemId).ToList(), ct); // batched reads spec §3
        var dates = new List<LocalDate> { today };
        var thisMonth = new YearMonth(today.Year, today.Month);
        for (var k = 1; k <= months; k++)
            dates.Add(thisMonth.PlusMonths(k).ToDateInterval().End);
        var monthly = dates.Select(_ => new Dictionary<Currency, decimal>()).ToList();
        var parts = dates.Select(_ => new List<TimelinePart>()).ToList();
        var extended = new List<ExtendedSource>();
        foreach (var source in found)
        {
            if (!accesses.TryGetValue(source.ItemId, out var item) || item.ArchivedAt is not null)
                continue;
            var currency = SourceCheck.CurrencyOf(item);
            IReadOnlyList<IncomePhase> list = bySource[source.ItemId].ToList();
            var wasExtended = false;
            var adults = item.Owners.Where(o => o.Kind == PersonKind.Adult).Select(o => o.PersonId);
            var owned = Departures.SwitchOf(adults, switches); // Retirement spec §2
            int? cutAt = owned >= 1 ? owned : null;
            var stops = cutAt is not null && RetirementSwitch.Stops(source.Kind);
            var isPension = source.Kind == IncomeKind.Pension;
            if (isPension && cutAt is { } at)
                list = RetirementSwitch.MovePension(list, today, thisMonth.PlusMonths(at).OnDayOfMonth(1));
            for (var k = 0; k < dates.Count; k++)
            {
                var sum = 0m;
                if (stops && k >= cutAt)
                {
                    monthly[k][currency] = monthly[k].GetValueOrDefault(currency);
                    continue;
                }
                foreach (var (phase, amount, isExtended) in IncomeMath.MonthlyEquivalentExtendedParts(list, today, dates[k], source.EndedOn is not null))
                {
                    // An extension keeps the month of the phase it extends.
                    var from = phase.StartsOn <= today ? 0 : (phase.StartsOn.Year - today.Year) * 12 + phase.StartsOn.Month - today.Month;
                    parts[k].Add(new TimelinePart(currency, amount, isPension ? 0 : from));
                    sum += amount;
                    wasExtended |= isExtended;
                }
                monthly[k][currency] = monthly[k].GetValueOrDefault(currency) + sum;
            }
            if (wasExtended)
                extended.Add(new ExtendedSource(source.ItemId, item.DisplayName));
        }
        return Result.Success(new IncomeTimeline(monthly.Select(m => (IReadOnlyDictionary<Currency, decimal>)m).ToList(), extended,
            parts.Select(p => (IReadOnlyList<TimelinePart>)p).ToList()));
    }
}

/// <summary>2D2a spec §4: the end of each running period of an open source that no phase of its label continues, and each <c>income_gap_soon</c> (shown in « À faire »).</summary>
public sealed class IncomeSourcesUpcoming(IVisibleItems visible, IItemRegistry items, ISourceStore sources, IPhaseStore phases) : IUpcomingEvents
{
    public async Task<Result<IReadOnlyList<UpcomingEvent>>> UpcomingAsync(PersonId reader, VisibilityScope scope, LocalDate today, LocalDate until, CancellationToken ct)
    {
        var found = await sources.FindManyAsync((await visible.ForAsync(reader, scope, ct)).ToList(), ct);
        var bySource = (await phases.ForSourcesAsync(found.Select(s => s.ItemId).ToList(), ct)).ToLookup(p => p.Source);
        var accesses = await items.FindManyAsync(found.Select(s => s.ItemId).ToList(), ct); // batched reads spec §3
        var events = new List<UpcomingEvent>();
        foreach (var source in found)
        {
            if (!accesses.TryGetValue(source.ItemId, out var item) || item.ArchivedAt is not null)
                continue;
            var list = bySource[source.ItemId].ToList();
            foreach (var phase in list)
                if (IncomeMath.IsActive(phase, today) && IncomeMath.EffectiveEnd(phase) is { } end && end <= until && !IncomeMath.IsContinued(phase, list))
                    events.Add(new UpcomingEvent(end, UpcomingKinds.IncomePhaseEnd, source.ItemId, "income", item.DisplayName, phase.Label, null));
            foreach (var gap in IncomeMath.Gaps(list, today))
                events.Add(new UpcomingEvent(gap.EndsOn, UpcomingKinds.IncomeGapSoon, source.ItemId, "income", item.DisplayName, gap.Label, null));
        }
        return Result.Success<IReadOnlyList<UpcomingEvent>>(events);
    }
}
