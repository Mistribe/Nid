using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.RealEstate.Application;

/// <summary>
/// Passive income spec C7, §3.3: the net rent by month of the visible let properties (archived included), batched as
/// <see cref="RealEstateRents"/>. An open range is read up to the month of today.
/// </summary>
public sealed class RealEstatePropertyIncome(IVisibleItems visible, IItemRegistry items, IPropertyStore properties, IFlows flows, IValuations valuations) : IPropertyIncome
{
    private static readonly IReadOnlySet<FlowKind> Kinds = new HashSet<FlowKind> { FlowKind.Income, FlowKind.Expense };

    private static DateRange Span(LocalDate start, LocalDate endBefore) => DateRange.Create(start, endBefore).TryGetValue(out var r) ? r : throw new InvalidOperationException();

    public async Task<Result<IReadOnlyList<PropertyIncomeSource>>> IncomeAsync(
        PersonId reader, VisibilityScope scope, DateRange range, IReadOnlyList<LocalDate> valueDates, LocalDate today, CancellationToken ct)
    {
        var last = range.EndBefore ?? today.With(DateAdjusters.StartOfMonth).PlusMonths(1);
        var found = await properties.FindManyAsync(await visible.ForAsync(reader, scope, includeArchived: true, ct), ct);
        var accesses = await items.FindManyAsync(found.Select(p => p.ItemId).ToList(), ct);
        // A tenancy overlaps [start, last) when it starts before the end and does not end before the start.
        var let = found.Where(p => accesses.ContainsKey(p.ItemId)
            && p.Tenancies.Any(t => t.Tenancy.StartsOn < last && (t.Tenancy.EndsOn is not { } end || end >= range.Start))).ToList();
        var ids = let.Select(p => p.ItemId).ToList();
        if (ids.Count == 0 || last <= range.Start) // an open range starting after today reads nothing
            return Result.Success<IReadOnlyList<PropertyIncomeSource>>([]);

        var listed = await flows.ListManyAsync(ids, Span(range.Start, last), Kinds, ct);
        if (listed.TryGetFailure(out var failure))
            return Result.Failure<IReadOnlyList<PropertyIncomeSource>>(failure!);
        var upTo = valueDates.Count == 0 ? today : valueDates.Max();
        var series = await valuations.SeriesManyAsync(ids, Span(LocalDate.MinIsoValue, upTo.PlusDays(1)), ct);
        if (series.TryGetFailure(out failure))
            return Result.Failure<IReadOnlyList<PropertyIncomeSource>>(failure!);
        listed.TryGetValue(out var flowLists);
        series.TryGetValue(out var values);

        var sources = new List<PropertyIncomeSource>();
        foreach (var property in let)
        {
            var item = accesses[property.ItemId];
            var tenancies = property.Tenancies.Select(t => t.Tenancy).ToList();
            var months = new List<PropertyIncomeMonth>();
            for (var month = range.Start.With(DateAdjusters.StartOfMonth); month < last; month = month.PlusMonths(1))
                months.Add(PropertyMath.IncomeIn(month, flowLists![property.ItemId], tenancies));
            // SeriesManyAsync keeps as_of order.
            var valueAt = valueDates.Distinct().ToDictionary(d => d, d => (decimal?)values![property.ItemId].LastOrDefault(v => v.AsOf <= d)?.Value.Amount);
            sources.Add(new PropertyIncomeSource(property.ItemId, item.DisplayName, LoanViews.CurrencyOf(item), item.ArchivedAt is not null,
                months, valueAt, tenancies.Any(t => PropertyMath.Covers(t, today))));
        }
        return Result.Success<IReadOnlyList<PropertyIncomeSource>>(sources);
    }
}
