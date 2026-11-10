using Nid.Core.Contracts;
using Nid.Income.Contracts;
using Nid.Income.Domain;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Income.Application;

/// <summary>The income overview's inputs, read once for the overview (<see cref="GetIncomeOverviewHandler"/>) and the annual review (<see cref="IncomeReview"/>).</summary>
public sealed class IncomeOverviewEntries(
    IVisibleItems visible, IItemRegistry items, ISourceStore sources, IPhaseStore phases, IFlows flows, IRentIncome rents,
    IHouseholdSettings household, IFxRates fx)
{
    /// <summary>
    /// The overview's inputs over [<paramref name="from"/>, <paramref name="to"/>], each due converted at its own date: the visible
    /// sources, ended and archived too, and the rents, weighted by <paramref name="person"/>'s share and narrowed by
    /// <paramref name="kind"/> and <paramref name="source"/>.
    /// </summary>
    internal async Task<Result<OverviewEntries>> ReadAsync(PersonId reader, VisibilityScope scope, PersonId? person, string? kind, ItemId? source,
        LocalDate from, LocalDate to, LocalDate today, CancellationToken ct)
    {
        var until = to.PlusDays(1);
        var span = DateRange.Create(from, until).TryGetValue(out var r) ? r : throw new InvalidOperationException("The overview's window always ends after it starts.");
        LocalDate[] at = [today, today.PlusYears(-1), today.PlusYears(-3)]; // the keys OverviewMath reads MonthlyAt by

        // Ended sources and archived properties too: their months still count (spec §4); only the rows leave them out.
        var found = await sources.FindManyAsync((await visible.ForAsync(reader, scope, includeArchived: true, ct)).ToList(), ct);
        var ids = found.Select(s => s.ItemId).ToList();
        var bySource = (await phases.ForSourcesAsync(ids, ct)).ToLookup(p => p.Source);
        var accesses = await items.FindManyAsync(ids, ct);
        var listed = await flows.ListManyAsync(ids, span, new HashSet<FlowKind> { FlowKind.Income }, ct);
        if (listed.TryGetFailure(out var failure)) return Result.Failure<OverviewEntries>(failure!);
        listed.TryGetValue(out var byItem);
        var rented = await rents.RentsAsync(reader, scope, span, at, today, ct);
        if (rented.TryGetFailure(out failure)) return Result.Failure<OverviewEntries>(failure!);
        rented.TryGetValue(out var properties);

        var reference = await household.ReferenceCurrencyAsync(ct);
        var foreign = accesses.Values.Select(SourceCheck.CurrencyOf).Concat(properties!.Select(p => p.Currency))
            .Where(c => c != reference).Distinct().ToList();
        // The reference's own quote is needed too: rates are per USD (RateTable converts through it).
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync([.. foreign, reference], LocalDate.Min(from, at.Min()), to, ct); // back to MonthlyAt's oldest date
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        decimal? Convert(decimal amount, Currency unit, LocalDate on)
        {
            if (rates.TryConvert(amount, unit, reference, on, projected: on > today, out var converted, out _))
                return converted;
            missing.Add(unit.Code); // the amount's currency, the one the reader knows: a reference-side miss names the reference otherwise
            return null;
        }

        // Kinds lists every kind the person's items have, before the kind and source filters narrow them.
        var kinds = new SortedSet<string>(StringComparer.Ordinal);
        bool Wanted(ItemId item, string itemKind)
        {
            kinds.Add(itemKind);
            return (kind is null || kind == itemKind) && (source is null || source == item);
        }

        var entries = new List<Entry>();
        foreach (var income in found)
        {
            if (!accesses.TryGetValue(income.ItemId, out var item)
                || Weight(person, item.Owners.Select(o => (o.PersonId, o.ShareBp))) is not { } weight
                || !Wanted(income.ItemId, IncomeWire.Of(income.Kind)))
                continue;
            var currency = SourceCheck.CurrencyOf(item);
            decimal? At(decimal amount, LocalDate on) => Convert(amount, currency, on) * weight;
            var list = bySource[income.ItemId].ToList();
            var next = IncomeMath.Next(list, today);
            entries.Add(new Entry(
                new OverviewInput(income.ItemId, item.DisplayName, IncomeWire.Of(income.Kind),
                    list.SelectMany(p => IncomeMath.Occurrences(p, from, until)).Select(o => new Due(o.On, At(o.Amount.Amount, o.On))).ToList(),
                    byItem!.GetValueOrDefault(income.ItemId, []).Where(f => f.Category == IncomeWire.OneOffCategory)
                        .Select(f => new Due(f.OccurredOn, At(f.Amount.Amount, f.OccurredOn))).ToList(),
                    at.ToDictionary(d => d, d => At(IncomeMath.MonthlyEquivalent(list, d), d))),
                "income", GetIncomeSourceHandler.Wire(item.Visibility), item.OwnerViews(), item.ArchivedAt is not null,
                next.Count == 0 ? null : next[0].On, next.Count == 0 ? null : At(next.Sum(o => o.Amount.Amount), next[0].On),
                IncomeMath.Gaps(list, today).FirstOrDefault()?.EndsOn, currency.Code));
        }
        foreach (var property in properties!)
        {
            if (Weight(person, property.Owners.Select(o => (o.PersonId, o.ShareBp))) is not { } weight
                || !Wanted(property.Item, GetIncomeOverviewHandler.RentKind))
                continue;
            decimal? At(decimal amount, LocalDate on) => Convert(amount, property.Currency, on) * weight;
            entries.Add(new Entry(
                new OverviewInput(property.Item, property.Name, GetIncomeOverviewHandler.RentKind,
                    property.Dues.Select(d => new Due(d.On, At(d.Amount, d.On))).ToList(), [],
                    at.ToDictionary(d => d, d => At(property.MonthlyAt[d], d))),
                "realestate", property.Visibility, property.Owners.Select(o => new ItemOwnerView(o.PersonId.Value, o.ShareBp)).ToList(),
                // Every lease over: no longer a source (docs/income-data-model.md); Next covers a current or later lease.
                property.Archived || (property.Next is null && property.MonthlyAt[today] == 0m), property.Next?.On, property.Next is { } n ? At(n.Amount, n.On) : null,
                property.EndsOn is { } end && end <= today.PlusDays(IncomeMath.GapWindowDays) ? end : null, property.Currency.Code));
        }
        return Result.Success(new OverviewEntries(reference, entries, kinds.ToList(), missing.ToList()));
    }

    /// <summary>The person's share of an item she owns (spec §4), 1 without a person, null when she does not own it.</summary>
    private static decimal? Weight(PersonId? person, IEnumerable<(PersonId Person, int ShareBp)> owners)
        => person is not { } p ? 1m : owners.Where(o => o.Person == p).Select(o => (decimal?)(o.ShareBp / 10_000m)).FirstOrDefault();

    internal sealed record OverviewEntries(Currency Reference, IReadOnlyList<Entry> Entries, IReadOnlyList<string> Kinds, IReadOnlyList<string> Missing);

    /// <param name="Currency">The item's own currency, the one its dues are converted from.</param>
    internal sealed record Entry(
        OverviewInput Input, string Module, string Visibility, IReadOnlyList<ItemOwnerView> Owners, bool Ended,
        LocalDate? NextOn, decimal? NextAmount, LocalDate? GapOn, string Currency);
}
