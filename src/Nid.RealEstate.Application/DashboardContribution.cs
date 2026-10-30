using System.Globalization;
using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.RealEstate.Application;

/// <summary>
/// 3E1 spec §2: each visible property for its latest estimate and each visible loan for − its remaining balance, one
/// <see cref="Holding"/> each, never netted (README §8), so the per-person split follows each item's own shares. Since
/// 1 January, a property's estimate change is a gain and a loan's principal repaid is paid in. Batched reads spec §3: the
/// items and the estimates are read once for all, whatever their number.
/// </summary>
public sealed class RealEstateNetWorth(IVisibleItems visible, IItemRegistry items, IPropertyStore properties, ILoanStore loans, IValuations valuations)
    : INetWorthContribution
{
    public async Task<Result<NetWorthPart>> NetWorthAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct)
    {
        var ids = await visible.ForAsync(reader, scope, ct);
        var found = await properties.FindManyAsync(ids, ct);
        var owing = await loans.FindManyAsync(ids, ct);
        var accesses = await items.FindManyAsync([.. found.Select(p => p.ItemId), .. owing.Select(l => l.ItemId)], ct);
        // Estimates that cannot be read count as no value, as an envelope's facts: never skipped, so no total is silently partial.
        var estimated = await DashboardParts.EstimatesAsync(valuations, found, accesses, ct);
        var january1 = new LocalDate(today.Year, 1, 1);
        var values = new Dictionary<Currency, decimal>();
        var starts = new Dictionary<Currency, decimal>();
        var nets = new Dictionary<Currency, decimal>();
        var gains = new Dictionary<Currency, decimal>();
        var holdings = new List<Holding>();
        int count = 0, withoutValue = 0, stale = 0, withoutStart = 0;
        LocalDate? oldest = null;

        foreach (var property in found)
        {
            if (!accesses.TryGetValue(property.ItemId, out var item))
                continue;
            count++;
            var currency = LoanViews.CurrencyOf(item);
            var estimates = estimated?.GetValueOrDefault(property.ItemId);
            var now = estimates is null ? null : PropertyMath.At(property.Details, false, estimates, [], [], [], today);
            if (estimates is null || now?.Value is not { } value)
            {
                withoutValue++;
                holdings.Add(new Holding(property.ItemId, "properties", item.DisplayName, null, null, false, DashboardParts.OwnersOf(item)));
                continue;
            }
            DashboardParts.Add(values, currency, value);
            if (now.ValueOn is { } on && (oldest is null || on < oldest))
                oldest = on;
            if (now.Stale)
                stale++;
            holdings.Add(new Holding(property.ItemId, "properties", item.DisplayName, PropertyFacts.MoneyOf(value, currency), now.ValueOn,
                now.Stale, DashboardParts.OwnersOf(item)));

            // As an envelope (2D2b decision 3): an estimate already stale on 1 January is no start. Nothing is paid into a property.
            var start = PropertyMath.At(property.Details, false, estimates, [], [], [], january1);
            if (start is { Value: { } startValue, Stale: false })
            {
                DashboardParts.Add(starts, currency, startValue);
                DashboardParts.Add(nets, currency, 0m);
                DashboardParts.Add(gains, currency, value - startValue);
            }
            else
                withoutStart++;
        }

        foreach (var loan in owing)
        {
            if (!accesses.TryGetValue(loan.ItemId, out var item))
                continue;
            var terms = LoanReading.Terms(LoanViews.CurrencyOf(item), loan.Contract, loan.Fees, loan.Repayments);
            var schedule = LoanReading.Schedule(terms, loan);
            var now = LoanMath.At(terms, schedule, today);
            if (now.Balance == 0m)
                continue; // repaid, or not yet disbursed: nothing owed, not listed (spec §2)
            count++;
            DashboardParts.Add(values, terms.Currency, -now.Balance);
            oldest ??= today; // every estimate is dated on or before today: today is never older
            holdings.Add(new Holding(loan.ItemId, "loans", item.DisplayName, PropertyFacts.MoneyOf(-now.Balance, terms.Currency), today, false,
                DashboardParts.OwnersOf(item)));

            // Owed on 1 January only once disbursed by then. The principal repaid since is paid in; the rest of the change is a
            // gain — 0, unless a total deferral capitalised interest (then a loss: the debt grew).
            if (terms.DisbursedOn <= january1)
            {
                var start = LoanMath.At(terms, schedule, january1);
                var repaid = now.PrincipalRepaid - start.PrincipalRepaid;
                DashboardParts.Add(starts, terms.Currency, -start.Balance);
                DashboardParts.Add(nets, terms.Currency, repaid);
                DashboardParts.Add(gains, terms.Currency, start.Balance - now.Balance - repaid);
            }
            else
                withoutStart++;
        }

        return Result.Success(new NetWorthPart(count, withoutValue, stale, oldest, values, starts, nets, gains, holdings, withoutStart));
    }

    /// <summary>Projection spec §2.1: a property is no savings; the loans' principal repaid is counted apart by the projection.</summary>
    public Task<Result<IReadOnlyDictionary<Currency, decimal>>> NetContributionsAsync(PersonId reader, VisibilityScope scope, LocalDate from, LocalDate to, CancellationToken ct)
        => Task.FromResult(Result.Success<IReadOnlyDictionary<Currency, decimal>>(new Dictionary<Currency, decimal>()));

    /// <summary>
    /// 3E1 spec §2: at each date, each property's latest estimate on or before it, and each loan's
    /// − remaining balance from its disbursement on, its principal repaid so far paid in — still after its end, so the
    /// paid-in line never drops when a loan is repaid. Before its first estimate, from its purchase on, a property counts at
    /// its purchase price: counting its loan alone drew a net worth deep below zero, then a jump on the day it was first
    /// estimated. A property never estimated stays out, as in today's net worth.
    /// </summary>
    public async Task<Result<IReadOnlyList<NetWorthPoint>>> NetWorthHistoryAsync(PersonId reader, VisibilityScope scope, IReadOnlyList<LocalDate> dates, CancellationToken ct)
    {
        var ids = await visible.ForAsync(reader, scope, ct);
        var found = await properties.FindManyAsync(ids, ct);
        var owing = await loans.FindManyAsync(ids, ct);
        var accesses = await items.FindManyAsync([.. found.Select(p => p.ItemId), .. owing.Select(l => l.ItemId)], ct);
        var estimates = await DashboardParts.EstimatesAsync(valuations, found, accesses, ct);
        var estimated = new List<(Currency Currency, IReadOnlyList<Valuation> Series, PropertyDetails Details)>();
        foreach (var property in found)
            if (accesses.TryGetValue(property.ItemId, out var item) && estimates?.GetValueOrDefault(property.ItemId) is { Count: > 0 } series)
                estimated.Add((LoanViews.CurrencyOf(item), series, property.Details));
        var owed = new List<(LoanTerms Terms, LoanSchedule Schedule)>();
        foreach (var loan in owing)
            if (accesses.TryGetValue(loan.ItemId, out var item))
            {
                var terms = LoanReading.Terms(LoanViews.CurrencyOf(item), loan.Contract, loan.Fees, loan.Repayments);
                owed.Add((terms, LoanReading.Schedule(terms, loan)));
            }

        var points = new List<NetWorthPoint>();
        foreach (var date in dates)
        {
            var values = new Dictionary<Currency, decimal>();
            var nets = new Dictionary<Currency, decimal>();
            var count = 0;
            foreach (var (currency, series, details) in estimated)
                if (series.LastOrDefault(v => v.AsOf <= date) is { } latest) // SeriesManyAsync keeps SeriesAsync's as_of order
                {
                    count++;
                    DashboardParts.Add(values, currency, latest.Value.Amount);
                }
                else if (date >= details.PurchaseDate)
                {
                    count++;
                    DashboardParts.Add(values, currency, details.PurchasePrice);
                }
            foreach (var (terms, schedule) in owed)
                if (date >= terms.DisbursedOn)
                {
                    var figures = LoanMath.At(terms, schedule, date);
                    count++;
                    DashboardParts.Add(values, terms.Currency, -figures.Balance);
                    DashboardParts.Add(nets, terms.Currency, figures.PrincipalRepaid);
                }
            points.Add(new NetWorthPoint(date, values, nets, count));
        }
        return Result.Success<IReadOnlyList<NetWorthPoint>>(points);
    }
}

/// <summary>What the module's dashboard parts share.</summary>
internal static class DashboardParts
{
    public static void Add(Dictionary<Currency, decimal> sums, Currency currency, decimal amount)
        => sums[currency] = sums.GetValueOrDefault(currency) + amount;

    public static IReadOnlyList<OwnerShare> OwnersOf(ItemAccess item) => item.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList();

    /// <summary>
    /// The current estimates by date of the properties the registry knows, read as the ambient actor in one block (batched reads
    /// spec §2); null when they cannot be read. Only known ids: one the registry does not know would refuse the whole block.
    /// </summary>
    public static async Task<IReadOnlyDictionary<ItemId, IReadOnlyList<Valuation>>?> EstimatesAsync(
        IValuations valuations, IEnumerable<StoredProperty> found, IReadOnlyDictionary<ItemId, ItemAccess> accesses, CancellationToken ct)
        => (await valuations.SeriesManyAsync(found.Select(p => p.ItemId).Where(accesses.ContainsKey).ToList(), PropertyReading.Everything, ct))
            .TryGetValue(out var series) ? series : null;
}

/// <summary>
/// 3E1 spec §3: the visible loans still running this month — amortized, the payment and insurance of the first row due this
/// month (nothing when none is); cash, every row due this month. A loan ended before this month counts nowhere.
/// </summary>
public sealed class LoanCommitments(IVisibleItems visible, IItemRegistry items, ILoanStore loans) : ICommitmentsContribution
{
    public async Task<Result<CommitmentsPart>> CommitmentsAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct)
    {
        var first = new LocalDate(today.Year, today.Month, 1);
        var last = first.PlusMonths(1).PlusDays(-1);
        var amortized = new Dictionary<Currency, decimal>();
        var cash = new Dictionary<Currency, decimal>();
        var count = 0;
        var found = await loans.FindManyAsync(await visible.ForAsync(reader, scope, ct), ct);
        var accesses = await items.FindManyAsync(found.Select(l => l.ItemId).ToList(), ct); // batched reads spec §3
        foreach (var loan in found)
        {
            if (!accesses.TryGetValue(loan.ItemId, out var item))
                continue;
            var terms = LoanReading.Terms(LoanViews.CurrencyOf(item), loan.Contract, loan.Fees, loan.Repayments);
            var schedule = LoanReading.Schedule(terms, loan);
            if (LoanReading.EndOf(terms, schedule) < first)
                continue;
            count++;
            var due = schedule.Rows.Where(r => r.DueDate >= first && r.DueDate <= last).ToList();
            if (due.Count == 0)
                continue;
            DashboardParts.Add(amortized, terms.Currency, due[0].Payment + due[0].Insurance);
            DashboardParts.Add(cash, terms.Currency, due.Sum(r => r.Payment + r.Insurance));
        }
        return Result.Success(new CommitmentsPart(CommitmentSources.Loans, count, amortized, cash));
    }
}

/// <summary>
/// 3E1 spec §3: the visible properties' planned costs — amortized, the monthly equivalents (3C2) of those running today; cash,
/// the occurrences dated this month (a cost comes every month or more: at most one).
/// </summary>
public sealed class PropertyCostCommitments(IVisibleItems visible, IItemRegistry items, IPropertyStore properties) : ICommitmentsContribution
{
    public async Task<Result<CommitmentsPart>> CommitmentsAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct)
    {
        var amortized = new Dictionary<Currency, decimal>();
        var cash = new Dictionary<Currency, decimal>();
        var count = 0;
        var found = await properties.FindManyAsync(await visible.ForAsync(reader, scope, ct), ct);
        var accesses = await items.FindManyAsync(found.Select(p => p.ItemId).ToList(), ct); // batched reads spec §3
        foreach (var property in found)
        {
            if (!accesses.TryGetValue(property.ItemId, out var item))
                continue;
            var currency = LoanViews.CurrencyOf(item);
            foreach (var cost in property.Costs.Select(c => c.Cost))
            {
                if (Running(cost, today))
                {
                    count++;
                    DashboardParts.Add(amortized, currency, PropertyMath.MonthlyEquivalent(cost));
                }
                if (PropertyMath.DueIn(cost, today) is not null)
                    DashboardParts.Add(cash, currency, cost.Amount);
            }
        }
        return Result.Success(new CommitmentsPart(CommitmentSources.PropertyCosts, count, amortized, cash));
    }

    private static bool Running(RecurringCost cost, LocalDate today) => cost.StartsOn <= today && (cost.EndsOn is not { } end || today < end);

    /// <summary>Projection spec §4.3: a property's planned costs a month, as the amortized commitments count them, in its currency.</summary>
    internal static decimal MonthlyCosts(StoredProperty property, LocalDate today)
        => property.Costs.Select(c => c.Cost).Where(c => Running(c, today)).Sum(PropertyMath.MonthlyEquivalent);
}

/// <summary>
/// 3E1 spec §4, over the visible loans and properties: a phase's end on the next phase's first payment (its rate or its deferral
/// changes), the last payment, the day the latest estimate turns stale, each planned cost's next due date with its amount, and a
/// tenancy's end. Batched reads spec §3: the items and the estimates are read once for all.
/// </summary>
public sealed class RealEstateUpcoming(IVisibleItems visible, IItemRegistry items, IPropertyStore properties, ILoanStore loans, IValuations valuations)
    : IUpcomingEvents
{
    public async Task<Result<IReadOnlyList<UpcomingEvent>>> UpcomingAsync(PersonId reader, VisibilityScope scope, LocalDate today, LocalDate until, CancellationToken ct)
    {
        var ids = await visible.ForAsync(reader, scope, ct);
        var owing = await loans.FindManyAsync(ids, ct);
        var found = await properties.FindManyAsync(ids, ct);
        var accesses = await items.FindManyAsync([.. owing.Select(l => l.ItemId), .. found.Select(p => p.ItemId)], ct);
        var events = new List<UpcomingEvent>();
        bool Within(LocalDate on) => on >= today && on <= until;

        foreach (var loan in owing)
        {
            if (!accesses.TryGetValue(loan.ItemId, out var item))
                continue;
            var terms = LoanReading.Terms(LoanViews.CurrencyOf(item), loan.Contract, loan.Fees, loan.Repayments);
            foreach (var change in LoanReading.Changes(LoanReading.Schedule(terms, loan).Rows).Where(c => Within(c.On)))
                events.Add(change.EndingPhase is { } phase
                    ? new UpcomingEvent(change.On, UpcomingKinds.LoanPhaseEnd, loan.ItemId, "loans", item.DisplayName,
                        phase.ToString(CultureInfo.InvariantCulture), null)
                    : new UpcomingEvent(change.On, UpcomingKinds.LoanEnd, loan.ItemId, "loans", item.DisplayName, null, null));
        }

        // Only a property with a staleness threshold has an estimate to redo; its latest on or before today is LatestAsync's.
        var estimates = await DashboardParts.EstimatesAsync(valuations,
            found.Where(p => PropertyMath.StaleThreshold(p.Details.Cadence, p.Details.StaleAfterDays) is not null), accesses, ct);
        foreach (var property in found)
        {
            if (!accesses.TryGetValue(property.ItemId, out var item))
                continue;
            var currency = LoanViews.CurrencyOf(item);
            if (PropertyMath.StaleThreshold(property.Details.Cadence, property.Details.StaleAfterDays) is { } days
                && estimates?.GetValueOrDefault(property.ItemId)?.LastOrDefault(v => v.AsOf <= today) is { } estimate)
            {
                // The first day PropertyMath.At calls it stale (more than the threshold since its date). Once stale it is in « À faire ».
                var due = estimate.AsOf.PlusDays(days + 1);
                if (due > today && due <= until)
                    events.Add(new UpcomingEvent(due, UpcomingKinds.ValuationDue, property.ItemId, "properties", item.DisplayName, null, null));
            }
            foreach (var cost in property.Costs.Select(c => c.Cost))
                if (PropertyMath.NextDueOn(cost, today) is { } next && next <= until)
                    events.Add(new UpcomingEvent(next, UpcomingKinds.PropertyCostDue, property.ItemId, "properties", item.DisplayName, cost.Category,
                        PropertyFacts.MoneyOf(cost.Amount, currency)));
            foreach (var tenancy in RentRevisions.LastLinks(property).Select(t => t.Tenancy))
                if (tenancy.EndsOn is { } ends && Within(ends))
                    events.Add(new UpcomingEvent(ends, UpcomingKinds.TenancyEnd, property.ItemId, "properties", item.DisplayName, tenancy.Label, null));
        }
        return Result.Success<IReadOnlyList<UpcomingEvent>>(events);
    }
}
