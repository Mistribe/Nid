using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.RealEstate.Application;

/// <summary>
/// Scenario spec §4.2 step 3: the visible properties at today's value as <see cref="RealEstateNetWorth"/> reads it, grown by
/// (1 + g)^(k/12), and the visible loans as the simulation reads them (<see cref="LoansOverviewMath.ProjectWithEvents"/>). An item
/// in another currency is converted at today's rate; without one it is left out and its currency named. Projection spec §5.2 adds
/// the loans' ends, the properties they free, and the scenario's early repayments and sales (§4.2, §4.3).
/// </summary>
public sealed class RealEstateScenarioProjection(
    IVisibleItems visible, IItemRegistry items, IPropertyStore properties, ILoanStore loans, IValuations valuations,
    IHouseholdSettings household, IFxRates fx) : IRealEstateProjection
{
    public async Task<Result<RealEstateProjection>> ProjectAsync(
        PersonId reader, VisibilityScope scope, LocalDate today, int months, decimal annualGrowth, RealEstateEvents events, CancellationToken ct)
    {
        var ids = await visible.ForAsync(reader, scope, ct);
        var found = await properties.FindManyAsync(ids, ct);
        var owing = await loans.FindManyAsync(ids, ct);
        var accesses = await items.FindManyAsync([.. found.Select(p => p.ItemId), .. owing.Select(l => l.ItemId)], ct);
        var estimated = await DashboardParts.EstimatesAsync(valuations, found, accesses, ct);

        var reference = await household.ReferenceCurrencyAsync(ct);
        var foreign = accesses.Values.Select(LoanViews.CurrencyOf).Where(c => c != reference).Distinct().ToList();
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync([.. foreign, reference], today, today, ct);
        var missing = foreign.Where(c => !rates.TryConvert(1m, c, reference, today, projected: false, out _, out _)).ToHashSet();

        // Each property's value today, converted; their sum in the same order as the dashboard's.
        var values = new Dictionary<ItemId, decimal>();
        var value = 0m;
        foreach (var property in found)
            if (accesses.TryGetValue(property.ItemId, out var item) && !missing.Contains(LoanViews.CurrencyOf(item))
                && estimated?.GetValueOrDefault(property.ItemId) is { } estimates
                && PropertyMath.At(property.Details, false, estimates, [], [], [], today)?.Value is { } now
                && rates.TryConvert(now, LoanViews.CurrencyOf(item), reference, today, projected: false, out var converted, out _))
            {
                values[property.ItemId] = converted;
                value += converted;
            }

        // Projection spec §6: an event on an item the reader does not see here is ignored (the caller has already dropped it).
        var thisMonth = today.ToYearMonth();
        // Point k is the last day of this month + k: an event dated in that month applies at k, one before today at 0, one past
        // the grid not at all.
        int? MonthOf(LocalDate on)
        {
            var k = on <= today ? 0 : Period.Between(thisMonth.OnDayOfMonth(1), on.With(DateAdjusters.StartOfMonth), PeriodUnits.Months).Months;
            return k <= months ? k : null;
        }
        LocalDate Dated(LocalDate on) => on < today ? today : on;
        var visibleLoans = owing.Where(l => accesses.ContainsKey(l.ItemId)).Select(l => l.ItemId).ToHashSet();
        var repayments = new List<LoanRepaymentEvent>();
        foreach (var e in events.Repayments)
            if (MonthOf(e.On) is { } k && Split(e.Split) is { } split && Effect(e.Effect) is { } effect
                && (split != SplitRule.Chosen || e.Loan is { } chosen && visibleLoans.Contains(chosen)))
                repayments.Add(new LoanRepaymentEvent(e.Event, k, Dated(e.On), e.Amount, split, e.Loan, effect));
        var sales = new List<(ProjectedSale Sale, LoanSaleEvent Event, StoredProperty Property)>();
        foreach (var e in events.Sales)
            if (MonthOf(e.On) is { } k && found.FirstOrDefault(p => p.ItemId == e.Property) is { } property && accesses.ContainsKey(property.ItemId)
                && sales.All(s => s.Property.ItemId != property.ItemId))
                sales.Add((e, new LoanSaleEvent(e.Event, k, Dated(e.On), property.ItemId), property));

        var loanMonths = LoansOverviewMath.ProjectWithEvents(
            owing.Where(l => accesses.ContainsKey(l.ItemId)).Select(l => new OverviewLoan(accesses[l.ItemId], l, null)).ToList(),
            missing, reference, rates, today, months, repayments, sales.Select(s => s.Event).ToList());

        var growth = (double)annualGrowth;
        decimal Grown(decimal amount, int k) => amount * (decimal)Math.Pow(1 + growth, k / 12.0);
        // A sold property leaves from its sale's month; without a sale the sum grows as one, as the purchase scenario reads it.
        decimal ValueAt(int k)
            => Grown(value - sales.Where(s => s.Event.Month <= k).Sum(s => values.GetValueOrDefault(s.Property.ItemId)), k);
        // Every rate missing was left out above, so no figure of the loans is null.
        var projected = loanMonths.Months.Select((m, k) => new RealEstateMonth(thisMonth.PlusMonths(k), ValueAt(k), m.Debt ?? 0m, m.Payments ?? 0m)).ToList();

        // Projection spec §4.3: price (or its projected value at the sale's month) − costs − the balance its loans were settled by;
        // MonthlyNet, its recurring costs running today (their monthly equivalents, as the dashboard's commitments) − today's rent.
        var outcomes = sales.Select(s =>
        {
            var price = s.Sale.Price ?? Grown(values.GetValueOrDefault(s.Property.ItemId), s.Event.Month);
            var currency = LoanViews.CurrencyOf(accesses[s.Property.ItemId]);
            var net = PropertyCostCommitments.MonthlyCosts(s.Property, today) - PropertyMath.MonthlyRentAt(s.Property.Tenancies.Select(t => t.Tenancy), today);
            var monthlyNet = !missing.Contains(currency) && rates.TryConvert(net, currency, reference, today, projected: false, out var converted, out _)
                ? converted : 0m;
            var (balance, payments) = loanMonths.Settled[s.Event.Event];
            return new SaleOutcome(s.Event.Event, s.Event.Month, price - s.Sale.Costs - balance, monthlyNet) { Settled = balance, LoanPayments = payments };
        }).ToList();

        // Projection spec §3.5: the milestones; a property is free when its last visible loan ends within the projection, before
        // its sale if it is sold (a sold property is not freed: its loans end with the sale).
        // Privacy trade-off (spec §6): « free » is computed from the reader's visible loans only. Another adult's private loan on a
        // shared property is deliberately left out, so the milestone may come earlier than the property is really free, rather
        // than reveal, by a later date, that a hidden loan exists.
        var ends = loanMonths.Ends;
        var soldOn = sales.ToDictionary(s => s.Property.ItemId, s => s.Event.On);
        var last = thisMonth.PlusMonths(months).ToDateInterval().End;
        var free = found
            .Where(p => accesses.ContainsKey(p.ItemId))
            .Select(p => (Property: p, Ends: ends.Where(e => e.Property == p.ItemId).ToList()))
            .Where(x => x.Ends.Count > 0 && x.Ends.All(e => e.On <= last))
            .Select(x => new PropertyFree(x.Property.ItemId, accesses[x.Property.ItemId].DisplayName, x.Ends.Max(e => e.On)))
            .Where(f => !soldOn.TryGetValue(f.Property, out var on) || f.On < on)
            .ToList();
        return Result.Success(new RealEstateProjection(projected, missing.Select(c => c.Code).Order(StringComparer.Ordinal).ToList())
        {
            LoanEnds = ends,
            PropertiesFree = free,
            Sales = outcomes,
            Repayments = loanMonths.Repayments,
        });
    }

    private static SplitRule? Split(string split) => split switch
    {
        ProjectionSplits.HighestRate => SplitRule.HighestRate,
        ProjectionSplits.SmallestBalance => SplitRule.SmallestBalance,
        ProjectionSplits.Chosen => SplitRule.Chosen,
        _ => null,
    };

    private static RepaymentEffect? Effect(string effect) => effect switch
    {
        ProjectionEffects.ReduceTerm => RepaymentEffect.ReduceTerm,
        ProjectionEffects.ReducePayment => RepaymentEffect.ReducePayment,
        _ => null,
    };
}
