using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.RealEstate.Application;

/// <summary>
/// 3E1 spec §2: each visible property for its latest estimate and each visible loan for − its remaining balance, one
/// <see cref="Holding"/> each, never netted (README §8), so the per-person split follows each item's own shares. Since
/// 1 January, a property's estimate change is a gain and a loan's principal repaid is paid in.
/// </summary>
public sealed class RealEstateNetWorth(IVisibleItems visible, IItemRegistry items, IPropertyStore properties, ILoanStore loans, IValuations valuations)
    : INetWorthContribution
{
    public async Task<Result<NetWorthPart>> NetWorthAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct)
    {
        var ids = await visible.ForAsync(reader, scope, ct);
        var january1 = new LocalDate(today.Year, 1, 1);
        var values = new Dictionary<Currency, decimal>();
        var starts = new Dictionary<Currency, decimal>();
        var nets = new Dictionary<Currency, decimal>();
        var gains = new Dictionary<Currency, decimal>();
        var holdings = new List<Holding>();
        int count = 0, withoutValue = 0, stale = 0, withoutStart = 0;
        LocalDate? oldest = null;

        foreach (var property in await properties.FindManyAsync(ids, ct))
        {
            if (await items.FindAsync(property.ItemId, ct) is not { } item)
                continue;
            count++;
            var currency = LoanViews.CurrencyOf(item);
            // Estimates that cannot be read count as no value, as an envelope's facts: never skipped, so no total is silently partial.
            var estimates = await DashboardParts.EstimatesAsync(valuations, property.ItemId, ct);
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

        foreach (var loan in await loans.FindManyAsync(ids, ct))
        {
            if (await items.FindAsync(loan.ItemId, ct) is not { } item)
                continue;
            var terms = LoanReading.Terms(LoanViews.CurrencyOf(item), loan.Contract, loan.Fees, loan.Repayments);
            var schedule = LoanReading.Schedule(terms);
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

    /// <summary>
    /// 3E1 spec §2: at each date, each property's latest estimate on or before it (none before its first), and each loan's
    /// − remaining balance from its disbursement on, its principal repaid so far paid in — still after its end, so the
    /// paid-in line never drops when a loan is repaid.
    /// </summary>
    public async Task<Result<IReadOnlyList<NetWorthPoint>>> NetWorthHistoryAsync(PersonId reader, VisibilityScope scope, IReadOnlyList<LocalDate> dates, CancellationToken ct)
    {
        var ids = await visible.ForAsync(reader, scope, ct);
        var estimated = new List<(Currency Currency, IReadOnlyList<Valuation> Series)>();
        foreach (var property in await properties.FindManyAsync(ids, ct))
            if (await items.FindAsync(property.ItemId, ct) is { } item && await DashboardParts.EstimatesAsync(valuations, property.ItemId, ct) is { } series)
                estimated.Add((LoanViews.CurrencyOf(item), series));
        var owed = new List<(LoanTerms Terms, LoanSchedule Schedule)>();
        foreach (var loan in await loans.FindManyAsync(ids, ct))
            if (await items.FindAsync(loan.ItemId, ct) is { } item)
            {
                var terms = LoanReading.Terms(LoanViews.CurrencyOf(item), loan.Contract, loan.Fees, loan.Repayments);
                owed.Add((terms, LoanReading.Schedule(terms)));
            }

        var points = new List<NetWorthPoint>();
        foreach (var date in dates)
        {
            var values = new Dictionary<Currency, decimal>();
            var nets = new Dictionary<Currency, decimal>();
            var count = 0;
            foreach (var (currency, series) in estimated)
                if (series.LastOrDefault(v => v.AsOf <= date) is { } latest) // SeriesAsync is ordered by as_of
                {
                    count++;
                    DashboardParts.Add(values, currency, latest.Value.Amount);
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

    /// <summary>A property's current estimates by date, read as the ambient actor; null when they cannot be read.</summary>
    public static async Task<IReadOnlyList<Valuation>?> EstimatesAsync(IValuations valuations, ItemId property, CancellationToken ct)
        => (await valuations.SeriesAsync(property, PropertyReading.Everything, ct)).TryGetValue(out var series) ? series : null;
}
