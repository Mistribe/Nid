using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.RealEstate.Application;

/// <summary>
/// Scenario spec §4.2 step 3: the visible properties at today's value as <see cref="RealEstateNetWorth"/> reads it, grown by
/// (1 + g)^(k/12), and the visible loans as the simulation reads them (<see cref="LoansOverviewMath.Project"/>). An item in
/// another currency is converted at today's rate; without one it is left out and its currency named. Projection spec §5.2 adds
/// the loans' ends and the properties they free.
/// </summary>
public sealed class RealEstateScenarioProjection(
    IVisibleItems visible, IItemRegistry items, IPropertyStore properties, ILoanStore loans, IValuations valuations,
    IHouseholdSettings household, IFxRates fx) : IRealEstateProjection
{
    public async Task<Result<RealEstateProjection>> ProjectAsync(
        PersonId reader, VisibilityScope scope, LocalDate today, int months, decimal annualGrowth, CancellationToken ct)
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

        var value = 0m;
        foreach (var property in found)
            if (accesses.TryGetValue(property.ItemId, out var item) && !missing.Contains(LoanViews.CurrencyOf(item))
                && estimated?.GetValueOrDefault(property.ItemId) is { } estimates
                && PropertyMath.At(property.Details, false, estimates, [], [], [], today)?.Value is { } now
                && rates.TryConvert(now, LoanViews.CurrencyOf(item), reference, today, projected: false, out var converted, out _))
                value += converted;

        var set = owing.Where(l => accesses.TryGetValue(l.ItemId, out var item) && !missing.Contains(LoanViews.CurrencyOf(item)))
            .Select(l => new OverviewLoan(accesses[l.ItemId], l, null)).ToList();
        var loanMonths = LoansOverviewMath.Project(set, reference, rates, today, months);

        var thisMonth = today.ToYearMonth();
        var growth = (double)annualGrowth;
        // Every rate missing was left out above, so no figure of the loans is null.
        var projected = loanMonths.Select((m, k) => new RealEstateMonth(thisMonth.PlusMonths(k),
            value * (decimal)Math.Pow(1 + growth, k / 12.0), m.Debt ?? 0m, m.Payments ?? 0m)).ToList();

        // Projection spec §3.5: the milestones; a property is free when its last visible loan ends within the projection.
        // Privacy trade-off (spec §6): « free » is computed from the reader's visible loans only. Another adult's private loan on a
        // shared property is deliberately left out, so the milestone may come earlier than the property is really free, rather
        // than reveal, by a later date, that a hidden loan exists.
        var ends = LoansOverviewMath.Ends(
            owing.Where(l => accesses.ContainsKey(l.ItemId)).Select(l => new OverviewLoan(accesses[l.ItemId], l, null)).ToList(), today);
        var last = thisMonth.PlusMonths(months).ToDateInterval().End;
        var free = found
            .Where(p => accesses.ContainsKey(p.ItemId))
            .Select(p => (Property: p, Ends: ends.Where(e => e.Property == p.ItemId).ToList()))
            .Where(x => x.Ends.Count > 0 && x.Ends.All(e => e.On <= last))
            .Select(x => new PropertyFree(x.Property.ItemId, accesses[x.Property.ItemId].DisplayName, x.Ends.Max(e => e.On)))
            .ToList();
        return Result.Success(new RealEstateProjection(projected, missing.Select(c => c.Code).Order(StringComparer.Ordinal).ToList())
        {
            LoanEnds = ends,
            PropertiesFree = free,
        });
    }
}
