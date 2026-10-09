using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application.Dashboard;

public enum NetWorthPeriod { TwelveMonths, ThreeYears, All }

/// <summary>2D2b spec §4: one point of the curve; both amounts null with no reading, or with a missing rate (then <c>MissingCurrency</c> names it).</summary>
public sealed record NetWorthPointView(LocalDate On, DashboardMoneyView? Value, DashboardMoneyView? Contributions, string? MissingCurrency);

public sealed record NetWorthHistoryView(string Currency, string Period, IReadOnlyList<NetWorthPointView> Points);

/// <summary>2D2b spec §4: the net worth curve for the reader, in the Foyer / Moi scope.</summary>
public sealed record GetNetWorthHistoryQuery(VisibilityScope Scope, NetWorthPeriod Period) : IQuery<NetWorthHistoryView>;

/// <summary>2D2b spec §4: the month-end grid and the conversion of each date at its own rate.</summary>
public static class NetWorthHistory
{
    public const int MaxYears = 20;

    public static string Code(NetWorthPeriod period) => period switch
    {
        NetWorthPeriod.TwelveMonths => "12m",
        NetWorthPeriod.ThreeYears => "3y",
        _ => "all",
    };

    /// <summary>The last day of each of the N months before today's, then today.</summary>
    public static IReadOnlyList<LocalDate> Grid(NetWorthPeriod period, LocalDate today)
    {
        var months = period switch { NetWorthPeriod.TwelveMonths => 12, NetWorthPeriod.ThreeYears => 36, _ => MaxYears * 12 };
        var firstOfMonth = new LocalDate(today.Year, today.Month, 1);
        var dates = new List<LocalDate>();
        for (var i = months; i >= 1; i--)
            dates.Add(firstOfMonth.PlusMonths(1 - i).PlusDays(-1));
        dates.Add(today);
        return dates;
    }

    public static NetWorthHistoryView Compose(
        NetWorthPeriod period, IReadOnlyList<LocalDate> grid, IReadOnlyList<IReadOnlyList<NetWorthPoint>> parts, Currency reference, RateTable rates)
    {
        var byDate = parts.SelectMany(p => p).GroupBy(p => p.On).ToDictionary(g => g.Key, g => g.ToList());
        var points = new List<NetWorthPointView>();
        foreach (var date in grid)
        {
            var at = byDate.GetValueOrDefault(date) ?? [];
            if (at.Sum(p => p.Envelopes) == 0)
            {
                points.Add(new NetWorthPointView(date, null, null, null));
                continue;
            }
            var value = DashboardMath.Sum.Of(DashboardMath.Merge(at.Select(p => p.Values)), reference, rates, date);
            var paid = DashboardMath.Sum.Of(DashboardMath.Merge(at.Select(p => p.NetContributions)), reference, rates, date);
            var missing = value.Missing ?? paid.Missing;
            points.Add(missing is null
                ? new NetWorthPointView(date, DashboardMath.Money(value.Total ?? 0m, reference), DashboardMath.Money(paid.Total ?? 0m, reference), null)
                : new NetWorthPointView(date, null, null, missing));
        }
        // « Tout » starts at the first month with a value, not 20 years of empty months.
        if (period == NetWorthPeriod.All)
            points = points.SkipWhile(p => p.Value is null && p.MissingCurrency is null).ToList();
        return new NetWorthHistoryView(reference.Code, Code(period), points);
    }
}

[RequestHandler<GetNetWorthHistoryQuery, NetWorthHistoryView>]
public sealed class GetNetWorthHistoryHandler(
    IEnumerable<INetWorthContribution> netWorth, IActorAccessor actor, IHouseholdCalendar calendar, IHouseholdSettings household, IFxRates fx)
    : IRequestHandler<GetNetWorthHistoryQuery, NetWorthHistoryView>
{
    public async ValueTask<Result<NetWorthHistoryView>> HandleAsync(GetNetWorthHistoryQuery query, CancellationToken ct = default)
    {
        var reader = actor.Current!.PersonId!.Value;
        var today = await calendar.TodayAsync(ct);
        var grid = NetWorthHistory.Grid(query.Period, today);

        var parts = new List<IReadOnlyList<NetWorthPoint>>();
        foreach (var contribution in netWorth)
        {
            var part = await contribution.NetWorthHistoryAsync(reader, query.Scope, grid, ct);
            if (part.TryGetFailure(out var failure)) return Result.Failure<NetWorthHistoryView>(failure!);
            part.TryGetValue(out var points);
            parts.Add(points!);
        }

        var reference = await household.ReferenceCurrencyAsync(ct);
        var foreign = parts.SelectMany(p => p).SelectMany(p => p.Values.Keys.Concat(p.NetContributions.Keys))
            .Where(c => c != reference).Distinct().ToList();
        // The reference's own quote too: rates are per USD. The loader reaches back MaxGapDays before the first date.
        var rates = foreign.Count == 0
            ? RateTable.Empty(today, 0)
            : await fx.LoadAsync(foreign.Append(reference).ToList(), grid[0], today, ct);
        return Result.Success(NetWorthHistory.Compose(query.Period, grid, parts, reference, rates));
    }
}
