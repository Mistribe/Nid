using Nid.Core.Application.Dashboard;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application.Reports;

/// <summary>Issue #12: the review of a year, the current one when <c>Year</c> is null.</summary>
public sealed record GetYearReviewQuery(int? Year, VisibilityScope Scope) : IQuery<YearReviewView>;

/// <summary>
/// Issue #12: a part of the net worth's change, under the key <see cref="MonthChanges.NetWorthParts"/> gives it. <c>Low</c> and
/// <c>High</c> are its bar: the running total from the start before and after it, in the order of the list.
/// </summary>
public sealed record ReviewPartView(string Key, DashboardMoneyView Amount, DashboardMoneyView Low, DashboardMoneyView High);

/// <summary>Issue #12: the net worth on <c>Start</c> and <c>End</c>, its change in parts, and the previous period's (null without one).</summary>
public sealed record NetWorthReviewView(
    DashboardMoneyView Start, DashboardMoneyView End, DashboardMoneyView Change, IReadOnlyList<ReviewPartView> Parts,
    DashboardMoneyView? PreviousChange, IReadOnlyList<ReviewPartView> PreviousParts);

/// <summary>Issue #12: one envelope of the year, in the household currency; <c>NoStartValue</c>: its start counted at the amount paid in.</summary>
public sealed record InvestmentsReviewRowView(
    ItemId Id, string Name, string Kind, DashboardMoneyView? StartValue, DashboardMoneyView? EndValue, DashboardMoneyView? NetContributions,
    DashboardMoneyView? Gain, decimal? PeriodReturnPct, bool StaleStart, bool NoStartValue);

/// <summary>Issue #12: the investments of the year as the Performance page counts them, and the previous period's gain and return.</summary>
public sealed record InvestmentsReviewView(
    DashboardMoneyView? StartValue, DashboardMoneyView? EndValue, DashboardMoneyView? Contributions, DashboardMoneyView? Withdrawals,
    DashboardMoneyView? Gain, decimal? PeriodReturnPct, DashboardMoneyView? PreviousGain, decimal? PreviousPeriodReturnPct,
    IReadOnlyList<InvestmentsReviewRowView> Envelopes, IReadOnlyList<string> Findings);

/// <summary>
/// Issue #12: the year from <c>Start</c> (31 December before it) to <c>End</c> (its 31 December, or today when <c>ToDate</c>);
/// <c>Years</c> are those the household can review. A section is null when it has nothing to show. <c>Findings</c> are the codes of
/// what the net worth could not convert (a missing rate, in this period or the previous one): the reason its section, or its previous
/// change, is absent.
/// </summary>
public sealed record YearReviewView(
    int Year, LocalDate Start, LocalDate End, bool ToDate, IReadOnlyList<int> Years,
    NetWorthReviewView? NetWorth, InvestmentsReviewView? Investments, IReadOnlyList<string> Findings) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Investments?.Envelopes.Select(e => e.Id) ?? [];
}

/// <summary>Issue #12: the net worth on two dates and its change in parts.</summary>
public sealed record NetWorthSplit(DashboardMoneyView Start, DashboardMoneyView End, DashboardMoneyView Change, IReadOnlyList<ReviewPartView> Parts);

public static class YearReviewMath
{
    /// <summary>How far back the years on offer reach.</summary>
    public const int MaxYears = 50;

    /// <summary>
    /// Each module's points on <paramref name="from"/> and <paramref name="to"/> as their parts, each date at its own rate, as the curve
    /// does, the largest first. A part is the step of the running total from the start, both ends rounded to the cent, so the parts add up
    /// to the change exactly; a step of zero is left out. Null when nothing was there at either date, or when a rate is missing (then
    /// <paramref name="missing"/> names its currency).
    /// </summary>
    public static NetWorthSplit? Split(
        IReadOnlyList<(string Module, NetWorthPoint From, NetWorthPoint To)> modules, Currency reference, RateTable rates, LocalDate from, LocalDate to,
        out string? missing)
    {
        missing = null;
        if (modules.Sum(m => m.From.Envelopes + m.To.Envelopes) == 0) return null;
        decimal start = 0m, end = 0m;
        var changes = new List<(string Key, decimal Change)>();
        foreach (var group in modules.SelectMany(m => MonthChanges.NetWorthParts(m.Module, m.From, m.To)).GroupBy(p => p.Key))
        {
            var b = DashboardMath.Sum.Of(DashboardMath.Merge(group.Select(p => p.Before)), reference, rates, from);
            var n = DashboardMath.Sum.Of(DashboardMath.Merge(group.Select(p => p.Now)), reference, rates, to);
            missing = b.Missing ?? n.Missing;
            if (missing is not null) return null;
            start += b.Total ?? 0m;
            end += n.Total ?? 0m;
            changes.Add((group.Key, (n.Total ?? 0m) - (b.Total ?? 0m)));
        }

        var parts = new List<ReviewPartView>();
        var running = start;
        foreach (var (key, change) in changes.OrderByDescending(c => Math.Abs(c.Change)).ThenBy(c => c.Key, StringComparer.Ordinal))
        {
            var before = Cents(running);
            running += change;
            var after = Cents(running);
            if (after == before) continue;
            parts.Add(new ReviewPartView(key, DashboardMath.Money(after - before, reference),
                DashboardMath.Money(Math.Min(before, after), reference), DashboardMath.Money(Math.Max(before, after), reference)));
        }
        // The running total ends on the end exactly: the parts are the same sums.
        return new NetWorthSplit(DashboardMath.Money(start, reference), DashboardMath.Money(end, reference),
            DashboardMath.Money(Cents(end) - Cents(start), reference), parts);
    }

    private static decimal Cents(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>The first year whose 31 December (today for the current year) has a holding, up to today's; today's year alone without any.</summary>
    public static IReadOnlyList<int> Years(IReadOnlyList<LocalDate> dates, IReadOnlyList<IReadOnlyList<NetWorthPoint>> points, int currentYear)
    {
        var first = dates.Where((_, i) => points.Any(p => p[i].Envelopes > 0)).Select(d => d.Year).DefaultIfEmpty(currentYear).First();
        return Enumerable.Range(first, currentYear - first + 1).ToList();
    }
}

[RequestHandler<GetYearReviewQuery, YearReviewView>]
public sealed class GetYearReviewHandler(
    IEnumerable<INetWorthContribution> netWorth, IEnumerable<IInvestmentsReview> investments, IActorAccessor actor, IHouseholdCalendar calendar,
    IHouseholdSettings household, IFxRates fx)
    : IRequestHandler<GetYearReviewQuery, YearReviewView>
{
    public async ValueTask<Result<YearReviewView>> HandleAsync(GetYearReviewQuery query, CancellationToken ct = default)
    {
        var reader = actor.Current!.PersonId!.Value;
        var today = await calendar.TodayAsync(ct);
        var sources = netWorth.ToList();

        var year = query.Year ?? today.Year;
        // The years on offer (31 December of each past year, and today) and the period's dates, read in one pass. A year outside the
        // scan is refused below: it reads the current year's dates meanwhile.
        var scan = Enumerable.Range(today.Year - YearReviewMath.MaxYears, YearReviewMath.MaxYears).Select(y => new LocalDate(y, 12, 31)).Append(today).ToList();
        var inScan = year <= today.Year && year >= today.Year - YearReviewMath.MaxYears;
        var toDate = year == today.Year;
        var start = new LocalDate((inScan ? year : today.Year) - 1, 12, 31);
        var end = toDate || !inScan ? today : new LocalDate(year, 12, 31);
        var (prevStart, prevEnd) = (start.PlusYears(-1), end.PlusYears(-1));
        LocalDate[] period = [prevStart, prevEnd, start, end];
        var dates = scan.Concat(period).Distinct().Order().ToList();

        var collected = await Contributions.CollectAsync(sources, c => c.NetWorthHistoryAsync(reader, query.Scope, dates, ct));
        if (collected.TryGetFailure(out var failure)) return Result.Failure<YearReviewView>(failure!);
        collected.TryGetValue(out var points);
        var years = YearReviewMath.Years(scan,
            points!.Select(p => (IReadOnlyList<NetWorthPoint>)scan.Select(d => p[dates.IndexOf(d)]).ToList()).ToList(), today.Year);

        if (year > today.Year || year < years[0])
            return Result.Failure<YearReviewView>(new Failure(ReportCodes.InvalidYear,
                $"A year is between {years[0]} and {today.Year}.", null));

        var reference = await household.ReferenceCurrencyAsync(ct);
        var foreign = points!.SelectMany(p => period.Select(d => p[dates.IndexOf(d)]))
            .SelectMany(p => p.Values.Keys.Concat(p.NetContributions.Keys).Concat(p.Moved?.Keys ?? []))
            .Where(c => c != reference).Distinct().ToList();
        // The reference's own quote too: rates are per USD.
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync(foreign.Append(reference).ToList(), prevStart, today, ct);

        var findings = new List<string>();
        NetWorthSplit? Split(LocalDate from, LocalDate to)
        {
            var split = YearReviewMath.Split(
                sources.Zip(points!, (s, p) => (s.Module, p[dates.IndexOf(from)], p[dates.IndexOf(to)])).ToList(), reference, rates, from, to, out var missing);
            if (missing is not null && !findings.Contains(FxCodes.RateMissing)) findings.Add(FxCodes.RateMissing);
            return split;
        }
        var current = Split(start, end);
        var previous = Split(prevStart, prevEnd);
        var worth = current is null ? null
            : new NetWorthReviewView(current.Start, current.End, current.Change, current.Parts, previous?.Change, previous?.Parts ?? []);

        InvestmentsReviewView? invested = null;
        if (investments.FirstOrDefault() is { } review)
        {
            var now = await review.ReviewAsync(reader, query.Scope, start, end, ct);
            if (now.TryGetFailure(out failure)) return Result.Failure<YearReviewView>(failure!);
            var before = await review.ReviewAsync(reader, query.Scope, prevStart, prevEnd, ct);
            if (before.TryGetFailure(out failure)) return Result.Failure<YearReviewView>(failure!);
            now.TryGetValue(out var n);
            before.TryGetValue(out var b);
            invested = n!.Envelopes.Count == 0 ? null : Investments(n, b!.Envelopes.Count == 0 ? null : b, reference);
        }

        return Result.Success(new YearReviewView(year, start, end, toDate, years, worth, invested, findings));
    }

    private static InvestmentsReviewView Investments(InvestmentsReviewPart now, InvestmentsReviewPart? before, Currency reference)
    {
        DashboardMoneyView? M(decimal? amount) => amount is { } a ? DashboardMath.Money(a, reference) : null;
        return new InvestmentsReviewView(
            M(now.StartValue), M(now.EndValue), M(now.Contributions), M(now.Withdrawals), M(now.Gain), now.PeriodReturnPct,
            M(before?.Gain), before?.PeriodReturnPct,
            now.Envelopes.Select(r => new InvestmentsReviewRowView(r.Id, r.Name, r.Kind, M(r.StartValue), M(r.EndValue), M(r.NetContributions),
                M(r.Gain), r.PeriodReturnPct, r.StaleStart, r.NoStartValue)).ToList(),
            now.Findings);
    }
}
