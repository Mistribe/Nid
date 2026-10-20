using System.Globalization;
using Nid.Core.Contracts;
using Nid.Income.Contracts;
using Nid.Income.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Income.Application;

/// <summary>
/// Income dashboard spec §4: the reader's household income over <paramref name="Period"/>, salaries and rents together.
/// <paramref name="Person"/> keeps that person's share of the items she owns; <paramref name="Kind"/> (an IncomeWire kind or
/// <c>rent</c>) and <paramref name="Source"/> narrow the inputs; <paramref name="IncludeEnded"/> only adds rows.
/// </summary>
public sealed record GetIncomeOverviewQuery(
    VisibilityScope Scope, OverviewPeriod Period, Guid? Person, string? Kind, ItemId? Source, bool IncludeEnded) : IQuery<IncomeOverview>;

/// <summary>Every amount is in <paramref name="Currency"/>, the household's; a null one could not be converted (<paramref name="MissingRates"/>).</summary>
public sealed record IncomeOverview(
    string Period, LocalDate From, LocalDate To, string Currency, IncomeOverviewFigures Figures,
    IReadOnlyList<IncomeOverviewShare> Split, IReadOnlyList<IncomeOverviewMonth> Months, IReadOnlyList<IncomeOverviewRow> Sources,
    IReadOnlyList<string> Kinds, IReadOnlyList<string> MissingRates) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems()
        => Sources.Select(s => s.Id)
            .Concat(Split.Where(s => s.Item is not null).Select(s => s.Item!.Value))
            .Concat(Months.SelectMany(m => m.BySource.Select(p => p.Item)))
            .Distinct();
}

public sealed record IncomeOverviewFigures(
    IncomeAmountView? MonthlyNow, decimal? MonthlyChange1y, IncomeAmountView? Total, IncomeAmountView? Elapsed,
    IncomeAmountView? ChangeVsPrevious, decimal? ChangeVsPreviousPct, decimal? Change3y, decimal? Change3yYearly);

/// <summary>A share of the split; <c>one_off</c> and <c>other</c> gather several items and carry none (their name is their kind).</summary>
public sealed record IncomeOverviewShare(ItemId? Item, string Name, string Kind, IncomeAmountView Total, IncomeAmountView PerMonth, decimal Percent);

public sealed record IncomeOverviewPart(ItemId Item, string Name, IncomeAmountView Amount);

public sealed record IncomeOverviewMonth(int Year, int Month, bool Upcoming, IncomeAmountView? Recurring, IncomeAmountView? OneOff,
    IncomeAmountView? Total, IncomeAmountView? PreviousYear, IReadOnlyList<IncomeOverviewPart> BySource);

/// <param name="Module"><c>income</c> for a source, <c>realestate</c> for a rented property.</param>
public sealed record IncomeOverviewRow(
    ItemId Id, string Module, string Name, string Kind, string Visibility, IReadOnlyList<ItemOwnerView> Owners,
    IncomeAmountView? PerMonth, decimal? Change1y, LocalDate? NextOn, IncomeAmountView? NextAmount, LocalDate? GapOn, bool Ended);

public static class OverviewPeriods
{
    public static string Of(OverviewPeriod period) => period switch
    {
        OverviewPeriod.Year => "year", OverviewPeriod.Last12Months => "12m", OverviewPeriod.ThreeYears => "3y",
        _ => throw new ArgumentOutOfRangeException(nameof(period)),
    };

    public static bool TryParse(string? wire, out OverviewPeriod period)
    {
        foreach (var p in Enum.GetValues<OverviewPeriod>())
            if (Of(p) == wire) { period = p; return true; }
        period = default;
        return false;
    }
}

[RequestHandler<GetIncomeOverviewQuery, IncomeOverview>]
public sealed class GetIncomeOverviewHandler(
    IVisibleItems visible, IItemRegistry items, ISourceStore sources, IPhaseStore phases, IFlows flows, IRentIncome rents,
    IHouseholdCalendar calendar, IHouseholdSettings household, IFxRates fx, IActorAccessor actor)
    : IRequestHandler<GetIncomeOverviewQuery, IncomeOverview>
{
    public const string RentKind = "rent";

    private static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    public async ValueTask<Result<IncomeOverview>> HandleAsync(GetIncomeOverviewQuery query, CancellationToken ct = default)
    {
        var reader = actor.Current!.PersonId!.Value;
        var today = await calendar.TodayAsync(ct);
        var window = OverviewMath.Window(query.Period, today);
        // Wide enough for every month's previous year and the previous window's total.
        var start = LocalDate.Min(window.PreviousFrom, window.From.PlusYears(-1));
        var until = window.To.PlusDays(1);
        var span = DateRange.Create(start, until).TryGetValue(out var r) ? r : throw new InvalidOperationException("The overview's window always ends after it starts.");
        LocalDate[] at = [today, today.PlusYears(-1), today.PlusYears(-3)]; // the keys OverviewMath reads MonthlyAt by

        // Ended sources and archived properties too: their months still count (spec §4); only the rows leave them out.
        var found = await sources.FindManyAsync((await visible.ForAsync(reader, query.Scope, includeArchived: true, ct)).ToList(), ct);
        var ids = found.Select(s => s.ItemId).ToList();
        var bySource = (await phases.ForSourcesAsync(ids, ct)).ToLookup(p => p.Source);
        var accesses = await items.FindManyAsync(ids, ct);
        var listed = await flows.ListManyAsync(ids, span, new HashSet<FlowKind> { FlowKind.Income }, ct);
        if (listed.TryGetFailure(out var failure)) return Result.Failure<IncomeOverview>(failure!);
        listed.TryGetValue(out var byItem);
        var rented = await rents.RentsAsync(reader, query.Scope, span, at, today, ct);
        if (rented.TryGetFailure(out failure)) return Result.Failure<IncomeOverview>(failure!);
        rented.TryGetValue(out var properties);

        var reference = await household.ReferenceCurrencyAsync(ct);
        var foreign = accesses.Values.Select(SourceCheck.CurrencyOf).Concat(properties!.Select(p => p.Currency))
            .Where(c => c != reference).Distinct().ToList();
        // The reference's own quote is needed too: rates are per USD (RateTable converts through it).
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync([.. foreign, reference], LocalDate.Min(start, at.Min()), window.To, ct); // back to MonthlyAt's oldest date
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        decimal? Convert(decimal amount, Currency from, LocalDate on)
        {
            if (rates.TryConvert(amount, from, reference, on, projected: on > today, out var converted, out _))
                return converted;
            missing.Add(from.Code); // the amount's currency, the one the reader knows: a reference-side miss names the reference otherwise
            return null;
        }

        // Kinds lists every kind the person's items have, before the kind and source filters narrow them.
        var kinds = new SortedSet<string>(StringComparer.Ordinal);
        bool Wanted(ItemId item, string kind)
        {
            kinds.Add(kind);
            return (query.Kind is null || query.Kind == kind) && (query.Source is null || query.Source == item);
        }

        var entries = new List<Entry>();
        foreach (var source in found)
        {
            if (!accesses.TryGetValue(source.ItemId, out var item)
                || Weight(query.Person, item.Owners.Select(o => (o.PersonId, o.ShareBp))) is not { } weight
                || !Wanted(source.ItemId, IncomeWire.Of(source.Kind)))
                continue;
            var currency = SourceCheck.CurrencyOf(item);
            decimal? At(decimal amount, LocalDate on) => Convert(amount, currency, on) * weight;
            var list = bySource[source.ItemId].ToList();
            var next = IncomeMath.Next(list, today);
            entries.Add(new Entry(
                new OverviewInput(source.ItemId, item.DisplayName, IncomeWire.Of(source.Kind),
                    list.SelectMany(p => IncomeMath.Occurrences(p, start, until)).Select(o => new Due(o.On, At(o.Amount.Amount, o.On))).ToList(),
                    byItem!.GetValueOrDefault(source.ItemId, []).Where(f => f.Category == IncomeWire.OneOffCategory)
                        .Select(f => new Due(f.OccurredOn, At(f.Amount.Amount, f.OccurredOn))).ToList(),
                    at.ToDictionary(d => d, d => At(IncomeMath.MonthlyEquivalent(list, d), d))),
                "income", GetIncomeSourceHandler.Wire(item.Visibility), item.OwnerViews(), item.ArchivedAt is not null,
                next.Count == 0 ? null : next[0].On, next.Count == 0 ? null : At(next.Sum(o => o.Amount.Amount), next[0].On),
                IncomeMath.Gaps(list, today).FirstOrDefault()?.EndsOn));
        }
        foreach (var property in properties!)
        {
            if (Weight(query.Person, property.Owners.Select(o => (o.PersonId, o.ShareBp))) is not { } weight
                || !Wanted(property.Item, RentKind))
                continue;
            decimal? At(decimal amount, LocalDate on) => Convert(amount, property.Currency, on) * weight;
            entries.Add(new Entry(
                new OverviewInput(property.Item, property.Name, RentKind,
                    property.Dues.Select(d => new Due(d.On, At(d.Amount, d.On))).ToList(), [],
                    at.ToDictionary(d => d, d => At(property.MonthlyAt[d], d))),
                "realestate", property.Visibility, property.Owners.Select(o => new ItemOwnerView(o.PersonId.Value, o.ShareBp)).ToList(),
                // Every lease over: no longer a source (docs/income-data-model.md); Next covers a current or later lease.
                property.Archived || (property.Next is null && property.MonthlyAt[today] == 0m), property.Next?.On, property.Next is { } n ? At(n.Amount, n.On) : null,
                property.EndsOn is { } end && end <= today.PlusDays(IncomeMath.GapWindowDays) ? end : null));
        }

        IncomeAmountView? Amount(decimal? amount) => amount is { } a ? IncomeAmountView.Of(a, reference) : null;
        var inputs = entries.Select(e => e.Input).ToList();
        var f = OverviewMath.Figures(inputs, window, today);
        return Result.Success(new IncomeOverview(
            OverviewPeriods.Of(query.Period), window.From, window.To, reference.Code,
            new IncomeOverviewFigures(Amount(f.MonthlyNow), f.MonthlyChange1y, Amount(f.Total), Amount(f.Elapsed),
                Amount(f.ChangeVsPrevious), f.ChangeVsPreviousPct, f.Change3y, f.Change3yYearly),
            OverviewMath.Split(inputs, window)
                .Select(s => new IncomeOverviewShare(s.Item, s.Name, s.Kind, Amount(s.Total)!, Amount(s.PerMonth)!, s.Percent)).ToList(),
            OverviewMath.Months(inputs, window, today)
                .Select(m => new IncomeOverviewMonth(m.Year, m.Month, m.Upcoming, Amount(m.Recurring), Amount(m.OneOff), Amount(m.Total),
                    Amount(m.PreviousYear), m.BySource.Select(b => new IncomeOverviewPart(b.Item, b.Name, Amount(b.Amount)!)).ToList()))
                .ToList(),
            entries.Where(e => query.IncludeEnded || !e.Ended)
                .Select(e => new IncomeOverviewRow(e.Input.Item, e.Module, e.Input.Name, e.Input.Kind, e.Visibility, e.Owners,
                    Amount(e.Input.MonthlyAt[today]), OverviewMath.ChangePct(e.Input.MonthlyAt[today], e.Input.MonthlyAt[at[1]]),
                    e.NextOn, Amount(e.NextAmount), e.GapOn, e.Ended))
                // A stable order: the name alone ties on a duplicate (as the sources' list).
                .OrderBy(row => row.Name, French).ThenBy(row => row.Id.Value).ToList(),
            kinds.ToList(), missing.ToList()));
    }

    /// <summary>The person's share of an item she owns (spec §4), 1 without a person, null when she does not own it.</summary>
    private static decimal? Weight(Guid? person, IEnumerable<(PersonId Person, int ShareBp)> owners)
        => person is not { } p ? 1m : owners.Where(o => o.Person.Value == p).Select(o => (decimal?)(o.ShareBp / 10_000m)).FirstOrDefault();

    private sealed record Entry(
        OverviewInput Input, string Module, string Visibility, IReadOnlyList<ItemOwnerView> Owners, bool Ended,
        LocalDate? NextOn, decimal? NextAmount, LocalDate? GapOn);
}
