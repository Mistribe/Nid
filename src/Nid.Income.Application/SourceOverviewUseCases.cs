using Nid.Core.Contracts;
using Nid.Income.Contracts;
using Nid.Income.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Income.Application;

/// <summary>Income dashboard spec §4: a source's page. <paramref name="Period"/> is <c>12m</c>, <c>3y</c> or <c>all</c>.</summary>
public sealed record GetIncomeSourceOverviewQuery(ItemId ItemId, string Period) : IQuery<IncomeSourceOverview>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

/// <summary>Every amount is in <paramref name="Currency"/>, the source's own; the months run to three months after today's.</summary>
public sealed record IncomeSourceOverview(
    ItemId Id, LocalDate From, LocalDate To, string Currency, IncomeSourceFigures Figures,
    IReadOnlyList<IncomeSourceMonth> Months, IReadOnlyList<IncomeChangeView> Changes,
    IReadOnlyList<IncomeOccurrenceView> LastPayments, IReadOnlyList<IncomeOneOffView> LastOneOffs,
    IReadOnlyList<IncomeWithholdingView> Withholding) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [Id];
}

/// <summary>
/// #408: an open salary's owner as a declarant of the household's tax estimate this year: the withholding rate against the real rate
/// (percents) and what the rate withholds each month on all of that person's salaries beyond the real rate (negative: short).
/// </summary>
public sealed record IncomeWithholdingView(Guid Person, string Name, decimal Rate, decimal RealRate, IncomeMoneyView MonthlyGap);

/// <summary>
/// <paramref name="MonthlyNow"/>, its <paramref name="MonthlyParts"/> (one per running label) and the changes are smoothed;
/// <paramref name="ReceivedThisYear"/> is cash from 1 January to today, one-offs included (<paramref name="OneOffsThisYear"/>).
/// <paramref name="RealChange1y"/> is <paramref name="Change1y"/> after <paramref name="Inflation"/>, the household's projection
/// assumption in percent (#407).
/// </summary>
public sealed record IncomeSourceFigures(
    IncomeAmountView MonthlyNow, IReadOnlyList<IncomeOverviewPart> MonthlyParts,
    LocalDate? NextOn, IncomeMoneyView? NextAmount, string? NextLabel,
    decimal? Change1y, decimal? ChangeSinceStart, IncomeMoneyView ReceivedThisYear, IncomeMoneyView OneOffsThisYear,
    decimal? RealChange1y, decimal Inflation);

public sealed record IncomeLabelAmount(string Label, IncomeMoneyView Amount);

public sealed record IncomeSourceMonth(int Year, int Month, bool Upcoming, IReadOnlyList<IncomeLabelAmount> Parts, IncomeMoneyView OneOff, IncomeMoneyView Total);

/// <summary>A dated step of the source's timeline (<see cref="PhaseChange"/>).</summary>
public sealed record IncomeChangeView(
    LocalDate On, string Kind, Guid PhaseId, string Label, IncomeMoneyView? Before, IncomeMoneyView? After, decimal? Percent, bool Upcoming);

[RequestHandler<GetIncomeSourceOverviewQuery, IncomeSourceOverview>]
public sealed class GetIncomeSourceOverviewHandler(
    IItemRegistry items, ISourceStore sources, IPhaseStore phases, IFlows flows, IOneOffTaxableStore taxables, IHouseholdCalendar calendar,
    IHouseholdSettings household, ITaxWithholdingRates withholding)
    : IRequestHandler<GetIncomeSourceOverviewQuery, IncomeSourceOverview>
{
    public const int UpcomingMonths = 3;
    public const int Last = 5;

    /// <summary>The months a period goes back, today's included; null for <c>all</c>, from the source's first month.</summary>
    private static bool TryParse(string period, out int? months)
    {
        months = period switch { "12m" => 12, "3y" => 36, _ => null };
        return months is not null || period == "all";
    }

    public async ValueTask<Result<IncomeSourceOverview>> HandleAsync(GetIncomeSourceOverviewQuery query, CancellationToken ct = default)
    {
        // The source first: an item that is not a source answers 404 whatever the period.
        var loaded = await SourceCheck.LoadAsync(sources, items, query.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<IncomeSourceOverview>(failure!);
        if (!TryParse(query.Period, out var span))
            return Result.Failure<IncomeSourceOverview>(new Failure(IncomeCodes.InvalidPeriod, "A period is 12m, 3y or all.", null));
        loaded.TryGetValue(out var source);
        var currency = SourceCheck.CurrencyOf(source.Item);
        var today = await calendar.TodayAsync(ct);
        var list = await phases.ForSourceAsync(query.ItemId, ct);
        var everything = DateRange.Create(LocalDate.MinIsoValue, null).TryGetValue(out var r) ? r : throw new InvalidOperationException();
        var listed = await GetIncomeSourceHandler.OneOffsAsync(flows, query.ItemId, everything, ct);
        if (listed.TryGetFailure(out failure))
            return Result.Failure<IncomeSourceOverview>(failure!);
        listed.TryGetValue(out var oneOffs);

        var thisMonth = new LocalDate(today.Year, today.Month, 1);
        var first = list.Select(p => p.StartsOn).Concat(oneOffs!.Select(f => f.OccurredOn)).DefaultIfEmpty(today).Min();
        var from = span is { } n ? thisMonth.PlusMonths(1 - n) : LocalDate.Min(new LocalDate(first.Year, first.Month, 1), thisMonth);
        var to = thisMonth.PlusMonths(UpcomingMonths + 1).PlusDays(-1);
        IncomeMoneyView Sum(IEnumerable<decimal> amounts)
            => IncomeMoneyView.From(Money.Create(amounts.Sum(), currency).TryGetValue(out var m) ? m : throw new InvalidOperationException());

        var lastOneOffs = oneOffs!.Where(f => f.OccurredOn <= today).OrderByDescending(f => f.OccurredOn).ThenByDescending(f => f.Id).Take(Last).ToList();
        var taxable = await taxables.ForFlowsAsync(lastOneOffs.Select(f => f.Id).ToList(), ct);
        var monthlyNow = IncomeMath.MonthlyEquivalent(list, today);
        var next = IncomeMath.Next(list, today);
        var yearStart = new LocalDate(today.Year, 1, 1);
        var inflation = (await household.ProjectionAssumptionsAsync(ct)).Inflation;
        var change1y = OverviewMath.ChangePct(monthlyNow, IncomeMath.MonthlyEquivalent(list, today.PlusYears(-1)));
        var paid = list.SelectMany(p => IncomeMath.Occurrences(p, LocalDate.MinIsoValue, today.PlusDays(1))).ToList();
        var oneOffsThisYear = oneOffs!.Where(f => f.OccurredOn >= yearStart && f.OccurredOn <= today).Select(f => f.Amount.Amount).ToList();
        var figures = new IncomeSourceFigures(
            IncomeAmountView.Of(monthlyNow, currency),
            list.Where(p => IncomeMath.IsActive(p, today)).GroupBy(p => p.Label).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new IncomeOverviewPart(query.ItemId, g.Key, IncomeAmountView.Of(g.Sum(IncomeMath.MonthlyEquivalent), currency)))
                .ToList(),
            next.Count == 0 ? null : next[0].On, next.Count == 0 ? null : Sum(next.Select(o => o.Amount.Amount)),
            next.Count == 0 ? null : string.Join(" + ", next.Select(o => o.Label).Distinct()),
            change1y,
            list.Count == 0 ? null : OverviewMath.ChangePct(monthlyNow, IncomeMath.MonthlyEquivalent(list, list.Min(p => p.StartsOn))),
            Sum(paid.Where(o => o.On >= yearStart).Select(o => o.Amount.Amount).Concat(oneOffsThisYear)),
            Sum(oneOffsThisYear), OverviewMath.RealChangePct(change1y, inflation), inflation * 100m);

        // The estimate counts open salaries in euros only (TaxLineCodes.InEuros).
        var rates = source.Source.Kind == IncomeKind.Salary && source.Item.ArchivedAt is null && currency == Currency.Eur
            ? (await withholding.RatesAsync(today, ct)).Where(r => source.Item.Owners.Any(o => o.PersonId == r.Person))
                .Select(r => new IncomeWithholdingView(r.Person.Value, r.Name, r.Rate, r.RealRate,
                    IncomeMoneyView.From(Money.Create(r.MonthlyGap, Currency.Eur).TryGetValue(out var gap) ? gap : throw new InvalidOperationException())))
                .ToList()
            : [];

        return Result.Success(new IncomeSourceOverview(
            query.ItemId, from, to, currency.Code, figures,
            IncomeMath.Expected(list, oneOffs!, from, to, currency).Select(m => new IncomeSourceMonth(
                m.Year, m.Month, new LocalDate(m.Year, m.Month, 1) > today,
                m.Occurrences.GroupBy(o => o.Label).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => new IncomeLabelAmount(g.Key, Sum(g.Select(o => o.Amount.Amount)))).ToList(),
                Sum(m.OneOffs.Select(f => f.Amount.Amount)), IncomeMoneyView.From(m.Total))).ToList(),
            IncomeMath.Changes(list, today).Select(c => new IncomeChangeView(c.On, c.Kind, c.PhaseId, c.Label,
                c.Before is { } before ? IncomeMoneyView.From(before) : null, c.After is { } after ? IncomeMoneyView.From(after) : null,
                c.Percent, c.Upcoming)).ToList(),
            paid.OrderByDescending(o => o.On).ThenBy(o => o.Label, StringComparer.Ordinal).Take(Last)
                .Select(o => new IncomeOccurrenceView(o.PhaseId, o.Label, o.On, IncomeMoneyView.From(o.Amount))).ToList(),
            lastOneOffs.Select(f => IncomeOneOffView.Of(f, taxable)).ToList(), rates));
    }
}
