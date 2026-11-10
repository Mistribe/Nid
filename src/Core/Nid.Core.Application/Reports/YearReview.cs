using Nid.Core.Application.Dashboard;
using Nid.Core.Application.Tax;
using Nid.Core.Contracts;
using Nid.Core.Domain.Tax;
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
    NetWorthReviewView? NetWorth, InvestmentsReviewView? Investments, IncomeReviewView? Income, IReadOnlyList<string> Findings) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems()
        => (Investments?.Envelopes.Select(e => e.Id) ?? []).Concat(Income?.Sources.Where(s => s.Id is not null).Select(s => s.Id!.Value) ?? []);
}

/// <summary>
/// Issue #12: the year's income as the income page counts it, its engagements (each month's cash due, and the tax estimate's months) and
/// the margin between them, beside the previous period's (null without one). An amount is null when a rate is missing.
/// </summary>
public sealed record IncomeReviewView(
    DashboardMoneyView? Income, IReadOnlyList<IncomeSourceView> Sources,
    DashboardMoneyView? Engagements, IReadOnlyList<EngagementSourceView> EngagementSources,
    DashboardMoneyView? Margin, DashboardMoneyView? PreviousIncome, DashboardMoneyView? PreviousEngagements, DashboardMoneyView? PreviousMargin);

/// <summary>Issue #12: one source of the year's income; <c>Key</c> is its item id, or <c>one_off</c> for the one-offs together.</summary>
public sealed record IncomeSourceView(ItemId? Id, string Key, string? Name, string? Kind, DashboardMoneyView? Amount);

/// <summary>Issue #12: one source of the year's engagements, one of <see cref="CommitmentSources"/>; <c>Estimated</c>: not yet known.</summary>
public sealed record EngagementSourceView(string Source, DashboardMoneyView? Amount, bool Estimated);

/// <summary>Issue #12: the engagements of a period in the household currency; <c>Missing</c> names a currency without a rate.</summary>
public sealed record EngagementsSum(decimal? Total, IReadOnlyList<(string Source, decimal? Amount, bool Estimated)> Sources, string? Missing);

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

    /// <summary>
    /// Each month's cash due by source, at the rate of its date (<paramref name="months"/>: one per month, read on that date), and the
    /// year's tax (in euros, as the estimate reads it) for as many twelfths as there are months, at the last month's rate; a refund
    /// commits nothing. A source counting no item is left out; one with a month still estimated is estimated, as the tax always is.
    /// </summary>
    public static EngagementsSum Engagements(
        IReadOnlyList<(LocalDate On, IReadOnlyList<CommitmentsPart> Parts)> months, decimal yearTax, Currency reference, RateTable rates)
    {
        string? missing = null;
        decimal? Convert(IReadOnlyDictionary<Currency, decimal> cash, LocalDate on)
        {
            var sum = DashboardMath.Sum.Of(cash, reference, rates, on);
            missing ??= sum.Missing;
            return sum.Missing is null ? sum.Total ?? 0m : null;
        }

        var sources = months.SelectMany(m => m.Parts.Select(p => (m.On, Part: p))).GroupBy(x => x.Part.Source)
            .Where(g => g.Sum(x => x.Part.Count) > 0)
            .Select(g => (Source: g.Key, Amount: g.Aggregate((decimal?)0m, (total, x) => Convert(x.Part.Cash, x.On) is { } a ? total + a : null),
                Estimated: g.Any(x => x.Part.Estimated)))
            .ToList();
        var tax = Math.Max(0m, yearTax) * months.Count / 12;
        if (tax > 0m) sources.Add((CommitmentSources.Tax, Convert(new Dictionary<Currency, decimal> { [Currency.Eur] = tax }, months[^1].On), true));
        // Each source to the cent, so the sources add up to the total shown.
        sources = sources.Select(s => s with { Amount = s.Amount is { } a ? Cents(a) : null })
            .OrderBy(s => DashboardMath.SourceRank(s.Source)).ThenBy(s => s.Source, StringComparer.Ordinal).ToList();
        return new EngagementsSum(sources.Any(s => s.Amount is null) ? null : sources.Sum(s => s.Amount!.Value), sources, missing);
    }

    /// <summary>
    /// The income section: null when nothing was earned nor committed. The margin is the income less the engagements, null when either
    /// is (a missing rate); the previous figures are null when the previous period had neither.
    /// </summary>
    public static IncomeReviewView? Income(
        IncomeReviewPart income, EngagementsSum engagements, IncomeReviewPart? previousIncome, EngagementsSum? previousEngagements, Currency reference)
    {
        if (income.Sources.Count == 0 && engagements.Sources.Count == 0) return null;
        DashboardMoneyView? M(decimal? amount) => amount is { } a ? DashboardMath.Money(a, reference) : null;
        // Both sides to the cent, so the margin is the difference of the figures shown.
        decimal? Margin(decimal? earned, decimal? committed) => earned is { } i && committed is { } c ? Cents(i) - Cents(c) : null;
        var before = previousIncome is { } p && previousEngagements is { } e && (p.Sources.Count > 0 || e.Sources.Count > 0);
        return new IncomeReviewView(
            M(income.Total), income.Sources.Select(s => new IncomeSourceView(s.Id, s.Key, s.Name, s.Kind, M(s.Amount))).ToList(),
            M(engagements.Total), engagements.Sources.Select(s => new EngagementSourceView(s.Source, M(s.Amount), s.Estimated)).ToList(),
            M(Margin(income.Total, engagements.Total)),
            before ? M(previousIncome!.Total) : null, before ? M(previousEngagements!.Total) : null,
            before ? M(Margin(previousIncome!.Total, previousEngagements!.Total)) : null);
    }

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
    IHouseholdSettings household, IFxRates fx, IEnumerable<IIncomeReview> incomes, IEnumerable<ICommitmentsContribution> commitments,
    TaxEstimates taxes)
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

        IncomeReviewPart none = new(0m, [], []);
        var earned = none;
        var earnedBefore = none;
        if (incomes.FirstOrDefault() is { } incomeReview)
        {
            var now = await incomeReview.ReviewAsync(reader, query.Scope, start, end, ct);
            if (now.TryGetFailure(out failure)) return Result.Failure<YearReviewView>(failure!);
            var before = await incomeReview.ReviewAsync(reader, query.Scope, prevStart, prevEnd, ct);
            if (before.TryGetFailure(out failure)) return Result.Failure<YearReviewView>(failure!);
            now.TryGetValue(out earned);
            before.TryGetValue(out earnedBefore);
        }
        var committed = await EngagementsAsync(reader, query.Scope, end, today, reference, ct);
        if (committed.TryGetFailure(out failure)) return Result.Failure<YearReviewView>(failure!);
        var committedBefore = await EngagementsAsync(reader, query.Scope, prevEnd, today, reference, ct);
        if (committedBefore.TryGetFailure(out failure)) return Result.Failure<YearReviewView>(failure!);
        committed.TryGetValue(out var spent);
        committedBefore.TryGetValue(out var spentBefore);
        if ((earned!.MissingCurrencies.Count > 0 || earnedBefore!.MissingCurrencies.Count > 0 || spent!.Missing is not null || spentBefore!.Missing is not null)
            && !findings.Contains(FxCodes.RateMissing))
            findings.Add(FxCodes.RateMissing);
        var income = YearReviewMath.Income(earned, spent!, earnedBefore, spentBefore, reference);

        return Result.Success(new YearReviewView(year, start, end, toDate, years, worth, invested, income, findings));
    }

    /// <summary>
    /// The engagements from January to <paramref name="end"/>'s month: each month's cash due, archived items included, read on its last
    /// day (on <paramref name="end"/> for its own month), and the year's tax estimate as the tax page computes it today (none before the rules).
    /// </summary>
    private async Task<Result<EngagementsSum>> EngagementsAsync(
        PersonId reader, VisibilityScope scope, LocalDate end, LocalDate today, Currency reference, CancellationToken ct)
    {
        // The tax part has no cash, and would estimate the year again for each month: its estimate is read once below.
        var cash = commitments.Where(c => c is not TaxCommitments).ToList();
        var months = new List<(LocalDate On, IReadOnlyList<CommitmentsPart> Parts)>();
        for (var month = 1; month <= end.Month; month++)
        {
            var on = month == end.Month ? end : new LocalDate(end.Year, month, 1).PlusMonths(1).PlusDays(-1);
            var parts = await Contributions.CollectAsync(cash, c => c.CommitmentsAsync(reader, scope, on, ct, includeArchived: true));
            if (parts.TryGetFailure(out var failure)) return Result.Failure<EngagementsSum>(failure!);
            parts.TryGetValue(out var read);
            months.Add((on, read!));
        }

        var tax = 0m;
        if (end.Year >= TaxRules.FirstYear)
        {
            var estimate = await taxes.ComputeAsync(end.Year, scope, today, ct);
            if (estimate.TryGetFailure(out var failure)) return Result.Failure<EngagementsSum>(failure!);
            estimate.TryGetValue(out var r);
            tax = r!.TotalTax;
        }

        var foreign = months.SelectMany(m => m.Parts).SelectMany(p => p.Cash.Keys).Concat(tax > 0m ? [Currency.Eur] : []).Where(x => x != reference).Distinct().ToList();
        // The reference's own quote too: rates are per USD.
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync(foreign.Append(reference).ToList(), months[0].On, end, ct);
        return Result.Success(YearReviewMath.Engagements(months, tax, reference, rates));
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
