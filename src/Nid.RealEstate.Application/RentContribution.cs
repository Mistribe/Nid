using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.RealEstate.Application;

/// <summary>
/// Income dashboard spec §5: the visible properties' rents, rent only, dated on the first covered day. <see cref="IRentIncome"/>
/// includes archived properties (so past months stay); <see cref="IIncomeContribution"/> (the home income) counts active ones only.
/// Batched reads as <see cref="PropertyCostCommitments"/>.
/// </summary>
public sealed class RealEstateRents(IVisibleItems visible, IItemRegistry items, IPropertyStore properties) : IRentIncome, IIncomeContribution
{
    public async Task<Result<IReadOnlyList<RentSource>>> RentsAsync(
        PersonId reader, VisibilityScope scope, DateRange range, IReadOnlyList<LocalDate> at, LocalDate today, CancellationToken ct)
    {
        var found = await properties.FindManyAsync(await visible.ForAsync(reader, scope, includeArchived: true, ct), ct);
        var accesses = await items.FindManyAsync(found.Select(p => p.ItemId).ToList(), ct);
        var last = range.EndBefore ?? today.With(DateAdjusters.StartOfMonth).PlusMonths(1);
        var sources = new List<RentSource>();
        foreach (var property in found)
        {
            if (property.Tenancies.Count == 0 || !accesses.TryGetValue(property.ItemId, out var item))
                continue;
            var tenancies = property.Tenancies.Select(t => t.Tenancy).ToList();
            var dues = new List<RentDue>();
            for (var month = range.Start.With(DateAdjusters.StartOfMonth); month < last; month = month.PlusMonths(1))
                foreach (var tenancy in tenancies)
                    if (PropertyMath.RentIn(tenancy, month, charges: false) is var (amount, from))
                        if (from >= range.Start && from < last)
                            dues.Add(new RentDue(from, amount));
            sources.Add(new RentSource(property.ItemId, item.DisplayName, LoanViews.CurrencyOf(item),
                item.Visibility == Visibility.Private ? "private" : "shared", DashboardParts.OwnersOf(item), item.ArchivedAt is not null,
                dues.OrderBy(d => d.On).ToList(), at.Distinct().ToDictionary(d => d, d => PropertyMath.MonthlyRentAt(tenancies, d)),
                NextDue(tenancies, today), EndOf(tenancies, today)));
        }
        return Result.Success<IReadOnlyList<RentSource>>(sources);
    }

    public string Module => "rents";

    /// <summary>
    /// Home income: the monthly rent (without charges) of each open visible property whose lease covers today or starts later;
    /// such a property puts an entry (0 before its lease starts) in its currency's sum, as <c>IncomeSourcesIncome</c> does.
    /// </summary>
    public async Task<Result<IncomePart>> IncomeAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct)
    {
        var found = await properties.FindManyAsync(await visible.ForAsync(reader, scope, ct), ct);
        var accesses = await items.FindManyAsync(found.Select(p => p.ItemId).ToList(), ct);
        var sums = new Dictionary<Currency, decimal>();
        var mine = new Dictionary<Currency, decimal>();
        var count = 0;
        foreach (var property in found)
        {
            var tenancies = property.Tenancies.Select(t => t.Tenancy).ToList();
            if (!tenancies.Any(t => t.StartsOn > today || PropertyMath.Covers(t, today)) || !accesses.TryGetValue(property.ItemId, out var item))
                continue;
            count++;
            var currency = LoanViews.CurrencyOf(item);
            var amount = PropertyMath.MonthlyRentAt(tenancies, today);
            sums[currency] = sums.GetValueOrDefault(currency) + amount;
            mine[currency] = mine.GetValueOrDefault(currency) + amount * item.ShareOf(reader) / 10_000m;
        }
        return Result.Success(new IncomePart(count, sums, mine));
    }

    /// <summary>
    /// Projection spec §3.2, §5.1: rents are assumed to continue, so today's rent repeats for every month and nothing is reported
    /// extended; a tenancy starting later is not anticipated (the rent changes the savings only through a revision the user records).
    /// </summary>
    public async Task<Result<IncomeTimeline>> TimelineAsync(PersonId reader, VisibilityScope scope, LocalDate today, int months, CancellationToken ct)
    {
        var income = await IncomeAsync(reader, scope, today, ct);
        if (income.TryGetFailure(out var failure))
            return Result.Failure<IncomeTimeline>(failure!);
        income.TryGetValue(out var part);
        return Result.Success(new IncomeTimeline(Enumerable.Repeat(part!.MonthlyEquivalent, months + 1).ToList(), []));
    }

    /// <summary>The end of the tenancy covering <paramref name="today"/> when it has one and no tenancy follows it.</summary>
    private static LocalDate? EndOf(IReadOnlyList<Tenancy> tenancies, LocalDate today)
        => tenancies.FirstOrDefault(t => PropertyMath.Covers(t, today))?.EndsOn is { } end && !tenancies.Any(t => t.StartsOn > end) ? end : null;

    /// <summary>The earliest due after <paramref name="today"/>: the month of the day after, or the one following, holds it.</summary>
    private static RentDue? NextDue(IReadOnlyList<Tenancy> tenancies, LocalDate today)
    {
        RentDue? next = null;
        foreach (var tenancy in tenancies)
        {
            var first = (tenancy.StartsOn > today ? tenancy.StartsOn : today.PlusDays(1)).With(DateAdjusters.StartOfMonth);
            foreach (var month in new[] { first, first.PlusMonths(1) })
                if (PropertyMath.RentIn(tenancy, month, charges: false) is var (amount, from) && from > today)
                {
                    if (next is null || from < next.On)
                        next = new RentDue(from, amount);
                    break;
                }
        }
        return next;
    }
}
