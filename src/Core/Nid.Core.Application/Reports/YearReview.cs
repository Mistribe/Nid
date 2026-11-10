using System.Globalization;
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

/// <summary>Issue #12: the engagements of a period in the household currency; <c>Missing</c> names a currency without a rate.</summary>
public sealed record EngagementsSum(decimal? Total, IReadOnlyList<(string Source, decimal? Amount, bool Estimated)> Sources, string? Missing);

/// <summary>Issue #12: the net worth on two dates and its change in parts.</summary>
public sealed record NetWorthSplit(DashboardMoneyView Start, DashboardMoneyView End, DashboardMoneyView Change, IReadOnlyList<ReviewPartView> Parts);

public static class YearReviewMath
{
    /// <summary>How far back the years on offer reach.</summary>
    public const int MaxYears = IIncomeReview.MaxYears;

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
    /// commits nothing. A source with no cash due and nothing estimated is left out (a loan not yet started); one with a month still
    /// estimated is estimated, as the tax always is.
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

        // The current month counts its whole cash due; the subscriptions section stops at today.
        var sources = months.SelectMany(m => m.Parts.Select(p => (m.On, Part: p))).GroupBy(x => x.Part.Source)
            .Where(g => g.Any(x => x.Part.Estimated || x.Part.Cash.Values.Any(v => v != 0m)))
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

    /// <summary>
    /// From the first year whose 31 December (today for the current year) has a holding, or the year of the first income due or charge
    /// (<paramref name="firstFlow"/>) when earlier, up to today's, at most <see cref="MaxYears"/> back; today's year alone without either.
    /// </summary>
    public static IReadOnlyList<int> Years(
        IReadOnlyList<LocalDate> dates, IReadOnlyList<IReadOnlyList<NetWorthPoint>> points, int currentYear, LocalDate? firstFlow = null)
    {
        var first = dates.Where((_, i) => points.Any(p => p[i].Envelopes > 0)).Select(d => d.Year).DefaultIfEmpty(currentYear).First();
        if (firstFlow is { } flow) first = Math.Max(currentYear - MaxYears, Math.Min(first, flow.Year));
        return Enumerable.Range(first, currentYear - first + 1).ToList();
    }

    /// <summary>The subscriptions section: null when the year had no charge, start, end nor price change; the previous cost likewise.</summary>
    public static SubscriptionsReviewView? Subscriptions(SubscriptionsReviewPart now, SubscriptionsReviewPart before, Currency reference)
    {
        // A cost of null is a charge without a rate: there was one.
        static bool Any(SubscriptionsReviewPart p) => p.Cost != 0m || p.Started.Count + p.Ended.Count + p.PriceChanges.Count > 0;
        if (!Any(now)) return null;
        static SubscriptionReviewItemView Item(SubscriptionReviewItem i) => new(i.Id, i.Name, i.Provider, i.On);
        // A price in its own currency and scale, as the subscription's page shows it.
        static DashboardMoneyView Price(decimal amount, string currency) => new(amount.ToString(CultureInfo.InvariantCulture), currency);
        return new SubscriptionsReviewView(M(now.Cost, reference), Any(before) ? M(before.Cost, reference) : null,
            now.Started.Select(Item).ToList(), now.Ended.Select(Item).ToList(),
            now.PriceChanges.Select(c => new SubscriptionPriceChangeView(c.Id, c.Name, c.On, Price(c.BeforeAmount, c.BeforeCurrency),
                ReviewDurationView.From(c.BeforeEvery), Price(c.AfterAmount, c.AfterCurrency), ReviewDurationView.From(c.AfterEvery), c.PromoEnded)).ToList());
    }

    /// <summary>The loans section: null without a loan in the year; the previous figures null without one in the previous period.</summary>
    public static LoansReviewView? Loans(LoansReviewPart now, LoansReviewPart before, Currency reference)
    {
        if (now.Loans.Count == 0) return null;
        var was = before.Loans.Count > 0;
        // Both to the cent, so the cost of credit is the sum of the figures the previous year showed.
        decimal? credit = was && before.Interest is { } i && before.Insurance is { } n ? Cents(i) + Cents(n) : null;
        return new LoansReviewView(M(now.Interest, reference), M(now.Insurance, reference), M(now.Fees, reference), M(now.CapitalRepaid, reference),
            M(now.StartBalance, reference), M(now.EndBalance, reference), M(credit, reference), was ? M(before.CapitalRepaid, reference) : null,
            now.Loans.Select(l => new LoanReviewRowView(l.Id, l.Name, M(l.Interest, reference), M(l.Insurance, reference), M(l.Fees, reference),
                M(l.CapitalRepaid, reference), M(l.StartBalance, reference), M(l.EndBalance, reference), l.ArchivedOn)).ToList());
    }

    /// <summary>The properties section: null without a property in the year; the previous figures null without one in the previous period.</summary>
    public static PropertiesReviewView? Properties(PropertiesReviewPart now, PropertiesReviewPart before, Currency reference)
    {
        if (now.Properties.Count == 0) return null;
        var was = before.Properties.Count > 0;
        return new PropertiesReviewView(M(now.Costs, reference), M(now.Rents, reference),
            was ? M(before.Costs, reference) : null, was ? M(before.Rents, reference) : null,
            now.Properties.Select(p => new PropertyReviewRowView(p.Id, p.Name, M(p.Costs, reference),
                p.ByCategory.Select(c => new PropertyCostCategoryView(c.Category, M(c.Amount, reference))).ToList(), M(p.Rents, reference),
                p.GrossYieldPct, p.NetYieldPct)).ToList());
    }

    /// <summary>The goals section: null without a goal in the year.</summary>
    public static GoalsReviewView? Goals(GoalsReviewPart part, Currency reference)
        => part.Goals.Count == 0 ? null
            : new GoalsReviewView(part.Goals.Select(g => new GoalReviewRowView(g.Id, g.Name, g.Kind, M(g.Target, reference), M(g.StartReached, reference),
                M(g.EndReached, reference), g.StartProgressPct, g.EndProgressPct, g.ReachedInPeriod, g.ArchivedOn, g.Partial)).ToList());

    /// <summary>
    /// The tax section: null when the year has no estimate (before the rules) or nothing to estimate from; the previous total null
    /// likewise. Every figure is in euros, converted at <paramref name="on"/>'s rate; <paramref name="missing"/> names a currency without one.
    /// </summary>
    public static TaxReviewView? Tax(TaxComputation? now, TaxComputation? before, Currency reference, RateTable rates, LocalDate on, out string? missing)
    {
        string? miss = null;
        DashboardMoneyView? Eur(decimal amount)
        {
            var sum = DashboardMath.Sum.Of(new Dictionary<Currency, decimal> { [Currency.Eur] = amount }, reference, rates, on);
            miss ??= sum.Missing;
            return sum.Missing is null ? DashboardMath.Money(sum.Total ?? 0m, reference) : null;
        }

        var view = now is { HasData: true }
            ? new TaxReviewView(Eur(now.Result.TotalTax), Eur(now.Result.IncomeTax), Eur(now.Result.SocialLevies), Eur(now.Result.CapitalFlatTax),
                before is { HasData: true } ? Eur(before.Result.TotalTax) : null)
            : null;
        missing = miss;
        return view;
    }

    private static DashboardMoneyView? M(decimal? amount, Currency reference) => amount is { } a ? DashboardMath.Money(a, reference) : null;
}

[RequestHandler<GetYearReviewQuery, YearReviewView>]
public sealed class GetYearReviewHandler(
    IEnumerable<INetWorthContribution> netWorth, IEnumerable<IInvestmentsReview> investments, IActorAccessor actor, IHouseholdCalendar calendar,
    IHouseholdSettings household, IFxRates fx, IEnumerable<IIncomeReview> incomes, IEnumerable<ICommitmentsContribution> commitments,
    TaxEstimates taxes, IEnumerable<ISubscriptionsReview> subscriptions, IEnumerable<ILoansReview> loans, IEnumerable<IPropertiesReview> properties,
    IEnumerable<IGoalsReview> goals)
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
        var firstDue = incomes.FirstOrDefault() is { } earning ? await earning.FirstDueAsync(reader, query.Scope, ct) : null;
        var firstCharge = subscriptions.FirstOrDefault() is { } paying ? await paying.FirstChargeAsync(reader, query.Scope, ct) : null;
        var years = YearReviewMath.Years(scan,
            points!.Select(p => (IReadOnlyList<NetWorthPoint>)scan.Select(d => p[dates.IndexOf(d)]).ToList()).ToList(), today.Year,
            new[] { firstDue, firstCharge }.Min());

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
        var income = YearReviewMath.Income(earned!, spent!, earnedBefore, spentBefore, reference);
        // A rate missing in a section not shown would point at nothing.
        var missing = income is not null
                      && (earned!.MissingCurrencies.Count > 0 || earnedBefore!.MissingCurrencies.Count > 0 || spent!.Missing is not null || spentBefore!.Missing is not null);

        var subscribed = await SectionAsync(subscriptions.FirstOrDefault(), (r, from, to) => r.ReviewAsync(reader, query.Scope, from, to, ct),
            p => p.MissingCurrencies, (n, b) => YearReviewMath.Subscriptions(n, b, reference), start, end);
        var borrowed = await SectionAsync(loans.FirstOrDefault(), (r, from, to) => r.ReviewAsync(reader, query.Scope, from, to, ct),
            p => p.MissingCurrencies, (n, b) => YearReviewMath.Loans(n, b, reference), start, end);
        var held = await SectionAsync(properties.FirstOrDefault(), (r, from, to) => r.ReviewAsync(reader, query.Scope, from, to, ct),
            p => p.MissingCurrencies, (n, b) => YearReviewMath.Properties(n, b, reference), start, end);
        if (subscribed.TryGetFailure(out failure) || borrowed.TryGetFailure(out failure) || held.TryGetFailure(out failure))
            return Result.Failure<YearReviewView>(failure!);
        subscribed.TryGetValue(out var s);
        borrowed.TryGetValue(out var l);
        held.TryGetValue(out var h);

        // The goals have no previous period: read once.
        GoalsReviewView? aimed = null;
        if (goals.FirstOrDefault() is { } goalsReview)
        {
            var read = await goalsReview.ReviewAsync(reader, query.Scope, start, end, ct);
            if (read.TryGetFailure(out failure)) return Result.Failure<YearReviewView>(failure!);
            read.TryGetValue(out var g);
            aimed = YearReviewMath.Goals(g!, reference);
            missing |= aimed is not null && g!.MissingCurrencies.Count > 0;
        }

        var taxed = await TaxAsync(year, query.Scope, start, end, today, reference, ct);
        if (taxed.TryGetFailure(out failure)) return Result.Failure<YearReviewView>(failure!);
        taxed.TryGetValue(out var t);

        if ((missing || s.Missing || l.Missing || h.Missing || t.Missing) && !findings.Contains(FxCodes.RateMissing)) findings.Add(FxCodes.RateMissing);

        return Result.Success(new YearReviewView(year, start, end, toDate, years, worth, invested, income, s.View, l.View, h.View, aimed, t.View, findings));
    }

    /// <summary>
    /// The year's tax estimate and the year before's total as the tax page computes them today (the engagements' own estimates, kept
    /// for the request), none before the rules, converted at <paramref name="end"/>'s rate.
    /// </summary>
    private async Task<Result<(TaxReviewView? View, bool Missing)>> TaxAsync(
        int year, VisibilityScope scope, LocalDate start, LocalDate end, LocalDate today, Currency reference, CancellationToken ct)
    {
        if (year < TaxRules.FirstYear) return Result.Success<(TaxReviewView?, bool)>((null, false));
        var now = await taxes.ComputeWithDataAsync(year, scope, today, ct);
        if (now.TryGetFailure(out var failure)) return Result.Failure<(TaxReviewView?, bool)>(failure!);
        now.TryGetValue(out var n);
        if (!n!.HasData) return Result.Success<(TaxReviewView?, bool)>((null, false));
        TaxComputation? b = null;
        if (year - 1 >= TaxRules.FirstYear)
        {
            var before = await taxes.ComputeWithDataAsync(year - 1, scope, today, ct);
            if (before.TryGetFailure(out failure)) return Result.Failure<(TaxReviewView?, bool)>(failure!);
            before.TryGetValue(out b);
        }
        // The reference's own quote too: rates are per USD.
        var rates = reference == Currency.Eur ? RateTable.Empty(today, 0) : await fx.LoadAsync([Currency.Eur, reference], start, end, ct);
        var view = YearReviewMath.Tax(n, b, reference, rates, end, out var missing);
        return Result.Success<(TaxReviewView?, bool)>((view, view is not null && missing is not null));
    }

    /// <summary>
    /// A section built from its part over the period and over the same dates a year earlier (none without a <paramref name="review"/>);
    /// <c>Missing</c>: the section is shown and either period missed a rate.
    /// </summary>
    private static async Task<Result<(TView? View, bool Missing)>> SectionAsync<TReview, TPart, TView>(
        TReview? review, Func<TReview, LocalDate, LocalDate, Task<Result<TPart>>> read, Func<TPart, IReadOnlyList<string>> missingOf,
        Func<TPart, TPart, TView?> map, LocalDate start, LocalDate end)
        where TReview : class where TPart : notnull where TView : class
    {
        if (review is null) return Result.Success<(TView?, bool)>((null, false));
        var now = await read(review, start, end);
        if (now.TryGetFailure(out var failure)) return Result.Failure<(TView?, bool)>(failure!);
        var before = await read(review, start.PlusYears(-1), end.PlusYears(-1));
        if (before.TryGetFailure(out failure)) return Result.Failure<(TView?, bool)>(failure!);
        now.TryGetValue(out var n);
        before.TryGetValue(out var b);
        var view = map(n!, b!);
        return Result.Success<(TView?, bool)>((view, view is not null && (missingOf(n!).Count > 0 || missingOf(b!).Count > 0)));
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
