using System.Globalization;
using Nid.Core.Contracts;
using Nid.Income.Contracts;
using Nid.Income.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Income.Application;

/// <summary>
/// Income dashboard spec §4: the reader's household income over <paramref name="Period"/>, salaries and rents together.
/// <paramref name="Person"/> keeps that person's share of the items she owns; <paramref name="Kind"/> (an IncomeWire kind or
/// <c>rent</c>) and <paramref name="Source"/> narrow the inputs; <paramref name="IncludeEnded"/> only adds rows. <paramref name="Year"/>
/// reads that calendar year (issue #12), with <see cref="OverviewPeriod.Year"/> only; the current one otherwise.
/// </summary>
public sealed record GetIncomeOverviewQuery(
    VisibilityScope Scope, OverviewPeriod Period, Guid? Person, string? Kind, ItemId? Source, bool IncludeEnded, int? Year = null)
    : IQuery<IncomeOverview>;

/// <summary>Every amount is in <paramref name="Currency"/>, the household's; a null one could not be converted (<paramref name="MissingRates"/>).</summary>
public sealed record IncomeOverview(
    string Period, LocalDate From, LocalDate To, string Currency, IncomeOverviewFigures Figures,
    IReadOnlyList<IncomeOverviewShare> Split, IReadOnlyList<IncomeOverviewNowShare> NowSplit, IReadOnlyList<IncomeOverviewMonth> Months, IReadOnlyList<IncomeOverviewRow> Sources,
    IReadOnlyList<string> Kinds, IReadOnlyList<string> MissingRates) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems()
        => Sources.Select(s => s.Id)
            .Concat(Split.Where(s => s.Item is not null).Select(s => s.Item!.Value))
            .Concat(NowSplit.Where(s => s.Item is not null).Select(s => s.Item!.Value))
            .Concat(Months.SelectMany(m => m.BySource.Select(p => p.Item)))
            .Distinct();
}

/// <summary><paramref name="MonthlyRealChange1y"/> is <paramref name="MonthlyChange1y"/> after <paramref name="Inflation"/>, the observed change over the last 12 months in percent (#407, #419); null when unknown.</summary>
public sealed record IncomeOverviewFigures(
    IncomeAmountView? MonthlyNow, decimal? MonthlyChange1y, IncomeAmountView? Total, IncomeAmountView? Elapsed,
    IncomeAmountView? ChangeVsPrevious, decimal? ChangeVsPreviousPct, decimal? Change3y, decimal? Change3yYearly,
    RealPercentView MonthlyRealChange1y, decimal? Inflation);

/// <summary>A share of the split; <c>one_off</c> and <c>other</c> gather several items and carry none (their name is their kind).</summary>
public sealed record IncomeOverviewShare(ItemId? Item, string Name, string Kind, IncomeAmountView Total, IncomeAmountView PerMonth, decimal Percent);

/// <summary>
/// A share of today's monthly income over the running sources and rents (the home's « Revenus / mois »), largest first;
/// <c>other</c> gathers the smallest and carries neither item nor module.
/// </summary>
public sealed record IncomeOverviewNowShare(ItemId? Item, string? Module, string Name, string Kind, IncomeAmountView PerMonth, decimal Percent);

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
    IncomeOverviewEntries overviewEntries, IHouseholdCalendar calendar, IActorAccessor actor, IInflation inflation)
    : IRequestHandler<GetIncomeOverviewQuery, IncomeOverview>
{
    public const string RentKind = "rent";

    private static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    /// <summary>How far back <see cref="GetIncomeOverviewQuery.Year"/> reaches, as the annual review's years.</summary>
    public const int MaxYearsBack = 50;

    public async ValueTask<Result<IncomeOverview>> HandleAsync(GetIncomeOverviewQuery query, CancellationToken ct = default)
    {
        var reader = actor.Current!.PersonId!.Value;
        var today = await calendar.TodayAsync(ct);
        if (query.Year is { } y && (query.Period != OverviewPeriod.Year || y > today.Year || y < today.Year - MaxYearsBack))
            return Result.Failure<IncomeOverview>(new Failure(IncomeCodes.InvalidYear,
                $"A year goes with the period year, between {today.Year - MaxYearsBack} and {today.Year}.", null));
        var window = OverviewMath.Window(query.Period, query.Year is { } year ? new LocalDate(year, 1, 1) : today);
        // Wide enough for every month's previous year and the previous window's total.
        var loaded = await overviewEntries.ReadAsync(reader, query.Scope, query.Person is { } p ? new PersonId(p) : null, query.Kind, query.Source,
            LocalDate.Min(window.PreviousFrom, window.From.PlusYears(-1)), window.To, today, ct);
        if (loaded.TryGetFailure(out var failure)) return Result.Failure<IncomeOverview>(failure!);
        loaded.TryGetValue(out var read);
        var (reference, entries, kinds, missing) = read!;

        IncomeAmountView? Amount(decimal? amount) => amount is { } a ? IncomeAmountView.Of(a, reference) : null;
        var inputs = entries.Select(e => e.Input).ToList();
        // The home's income counts open sources and active properties only (DashboardContribution, RentContribution).
        var running = entries.Where(e => !e.Ended).ToList();
        var modules = running.ToDictionary(e => e.Input.Item, e => e.Module);
        var f = OverviewMath.Figures(inputs, window, today);
        var series = await inflation.ReadAsync(ct);
        return Result.Success(new IncomeOverview(
            OverviewPeriods.Of(query.Period), window.From, window.To, reference.Code,
            new IncomeOverviewFigures(Amount(f.MonthlyNow), f.MonthlyChange1y, Amount(f.Total), Amount(f.Elapsed),
                Amount(f.ChangeVsPrevious), f.ChangeVsPreviousPct, f.Change3y, f.Change3yYearly,
                series.RealChangeOver12Months(f.MonthlyChange1y, reference), OverviewMath.InflationPct(series)),
            OverviewMath.Split(inputs, window)
                .Select(s => new IncomeOverviewShare(s.Item, s.Name, s.Kind, Amount(s.Total)!, Amount(s.PerMonth)!, s.Percent)).ToList(),
            OverviewMath.NowSplit(running.Select(e => e.Input).ToList(), today)
                .Select(s => new IncomeOverviewNowShare(s.Item, s.Item is { } id ? modules[id] : null, s.Name, s.Kind, Amount(s.PerMonth)!, s.Percent)).ToList(),
            OverviewMath.Months(inputs, window, today)
                .Select(m => new IncomeOverviewMonth(m.Year, m.Month, m.Upcoming, Amount(m.Recurring), Amount(m.OneOff), Amount(m.Total),
                    Amount(m.PreviousYear), m.BySource.Select(b => new IncomeOverviewPart(b.Item, b.Name, Amount(b.Amount)!)).ToList()))
                .ToList(),
            entries.Where(e => query.IncludeEnded || !e.Ended)
                .Select(e => new IncomeOverviewRow(e.Input.Item, e.Module, e.Input.Name, e.Input.Kind, e.Visibility, e.Owners,
                    Amount(e.Input.MonthlyAt[today]), OverviewMath.ChangePct(e.Input.MonthlyAt[today], e.Input.MonthlyAt[today.PlusYears(-1)]),
                    e.NextOn, Amount(e.NextAmount), e.GapOn, e.Ended))
                // A stable order: the name alone ties on a duplicate (as the sources' list).
                .OrderBy(row => row.Name, French).ThenBy(row => row.Id.Value).ToList(),
            kinds, missing));
    }
}
