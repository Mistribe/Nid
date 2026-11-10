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
/// what a section could not convert (a missing rate, in this period or the previous one): the reason the net worth, or a figure, is absent.
/// </summary>
public sealed record YearReviewView(
    int Year, LocalDate Start, LocalDate End, bool ToDate, IReadOnlyList<int> Years,
    NetWorthReviewView? NetWorth, InvestmentsReviewView? Investments, IncomeReviewView? Income,
    SubscriptionsReviewView? Subscriptions, LoansReviewView? Loans, PropertiesReviewView? Properties, IReadOnlyList<string> Findings) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems()
        => (Investments?.Envelopes.Select(e => e.Id) ?? []).Concat(Income?.Sources.Where(s => s.Id is not null).Select(s => s.Id!.Value) ?? [])
            .Concat(Subscriptions is { } s ? s.Started.Select(i => i.Id).Concat(s.Ended.Select(i => i.Id)).Concat(s.PriceChanges.Select(c => c.Id)) : [])
            .Concat(Loans?.Loans.Select(l => l.Id) ?? []).Concat(Properties?.Properties.Select(p => p.Id) ?? []);
}

/// <summary>Issue #12: the year's subscriptions: the cost of its charges, those started, ended and repriced, and the previous period's cost.</summary>
public sealed record SubscriptionsReviewView(
    DashboardMoneyView? Cost, DashboardMoneyView? PreviousCost, IReadOnlyList<SubscriptionReviewItemView> Started,
    IReadOnlyList<SubscriptionReviewItemView> Ended, IReadOnlyList<SubscriptionPriceChangeView> PriceChanges);

/// <summary>Issue #12: a subscription that started (its first charge) or ended (its last day covered) on <c>On</c>.</summary>
public sealed record SubscriptionReviewItemView(ItemId Id, string Name, string? Provider, LocalDate On);

/// <summary>Issue #12: a charging cycle, <c>Unit</c> one of day, week, month, year.</summary>
public sealed record ReviewDurationView(int Count, string Unit)
{
    public static ReviewDurationView From(Nid.Core.Contracts.Duration d) => new(d.Count, d.Unit.ToString().ToLowerInvariant());
}

/// <summary>Issue #12: a new price or cycle from <c>On</c>, in the subscription's currency; <c>PromoEnded</c>: the earlier one had a cycle limit.</summary>
public sealed record SubscriptionPriceChangeView(
    ItemId Id, string Name, LocalDate On, DashboardMoneyView Before, ReviewDurationView BeforeEvery, DashboardMoneyView After,
    ReviewDurationView AfterEvery, bool PromoEnded);

/// <summary>Issue #12: the year's loans as their schedules count them, and the previous period's cost of credit and capital repaid.</summary>
public sealed record LoansReviewView(
    DashboardMoneyView? Interest, DashboardMoneyView? Insurance, DashboardMoneyView? Fees, DashboardMoneyView? CapitalRepaid,
    DashboardMoneyView? StartBalance, DashboardMoneyView? EndBalance, DashboardMoneyView? PreviousInterestAndInsurance,
    DashboardMoneyView? PreviousCapitalRepaid, IReadOnlyList<LoanReviewRowView> Loans);

/// <summary>Issue #12: a loan of the year; <c>ArchivedOn</c>: archived in it, its end balance then 0.</summary>
public sealed record LoanReviewRowView(
    ItemId Id, string Name, DashboardMoneyView? Interest, DashboardMoneyView? Insurance, DashboardMoneyView? Fees, DashboardMoneyView? CapitalRepaid,
    DashboardMoneyView? StartBalance, DashboardMoneyView? EndBalance, LocalDate? ArchivedOn);

/// <summary>Issue #12: the year's property costs and rents, and the previous period's.</summary>
public sealed record PropertiesReviewView(
    DashboardMoneyView? Costs, DashboardMoneyView? Rents, DashboardMoneyView? PreviousCosts, DashboardMoneyView? PreviousRents,
    IReadOnlyList<PropertyReviewRowView> Properties);

/// <summary>Issue #12: a property of the year: its costs by category, largest first, its rents and its yields over the year (null: not let before it).</summary>
public sealed record PropertyReviewRowView(
    ItemId Id, string Name, DashboardMoneyView? Costs, IReadOnlyList<PropertyCostCategoryView> ByCategory, DashboardMoneyView? Rents,
    decimal? GrossYieldPct, decimal? NetYieldPct);

/// <summary>Issue #12: an expense category (<c>other</c> without one) and what it paid in the year.</summary>
public sealed record PropertyCostCategoryView(string Category, DashboardMoneyView? Amount);

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

    private static DashboardMoneyView? M(decimal? amount, Currency reference) => amount is { } a ? DashboardMath.Money(a, reference) : null;
}

[RequestHandler<GetYearReviewQuery, YearReviewView>]
public sealed class GetYearReviewHandler(
    IEnumerable<INetWorthContribution> netWorth, IEnumerable<IInvestmentsReview> investments, IActorAccessor actor, IHouseholdCalendar calendar,
    IHouseholdSettings household, IFxRates fx, IEnumerable<IIncomeReview> incomes, IEnumerable<ICommitmentsContribution> commitments,
    TaxEstimates taxes, IEnumerable<ISubscriptionsReview> subscriptions, IEnumerable<ILoansReview> loans, IEnumerable<IPropertiesReview> properties)
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
        var missing = earned!.MissingCurrencies.Count > 0 || earnedBefore!.MissingCurrencies.Count > 0 || spent!.Missing is not null || spentBefore!.Missing is not null;
        var income = YearReviewMath.Income(earned, spent!, earnedBefore, spentBefore, reference);

        SubscriptionsReviewView? subscribed = null;
        if (subscriptions.FirstOrDefault() is { } subscriptionsReview)
        {
            var read = await PeriodsAsync((from, to) => subscriptionsReview.ReviewAsync(reader, query.Scope, from, to, ct), start, end);
            if (read.TryGetFailure(out failure)) return Result.Failure<YearReviewView>(failure!);
            read.TryGetValue(out var p);
            missing |= p.Now.MissingCurrencies.Count > 0 || p.Before.MissingCurrencies.Count > 0;
            subscribed = YearReviewMath.Subscriptions(p.Now, p.Before, reference);
        }
        LoansReviewView? borrowed = null;
        if (loans.FirstOrDefault() is { } loansReview)
        {
            var read = await PeriodsAsync((from, to) => loansReview.ReviewAsync(reader, query.Scope, from, to, ct), start, end);
            if (read.TryGetFailure(out failure)) return Result.Failure<YearReviewView>(failure!);
            read.TryGetValue(out var p);
            missing |= p.Now.MissingCurrencies.Count > 0 || p.Before.MissingCurrencies.Count > 0;
            borrowed = YearReviewMath.Loans(p.Now, p.Before, reference);
        }
        PropertiesReviewView? held = null;
        if (properties.FirstOrDefault() is { } propertiesReview)
        {
            var read = await PeriodsAsync((from, to) => propertiesReview.ReviewAsync(reader, query.Scope, from, to, ct), start, end);
            if (read.TryGetFailure(out failure)) return Result.Failure<YearReviewView>(failure!);
            read.TryGetValue(out var p);
            missing |= p.Now.MissingCurrencies.Count > 0 || p.Before.MissingCurrencies.Count > 0;
            held = YearReviewMath.Properties(p.Now, p.Before, reference);
        }
        if (missing && !findings.Contains(FxCodes.RateMissing)) findings.Add(FxCodes.RateMissing);

        return Result.Success(new YearReviewView(year, start, end, toDate, years, worth, invested, income, subscribed, borrowed, held, findings));
    }

    /// <summary>A part over the period from <paramref name="start"/> to <paramref name="end"/>, and over the same dates a year earlier.</summary>
    private static async Task<Result<(T Now, T Before)>> PeriodsAsync<T>(Func<LocalDate, LocalDate, Task<Result<T>>> review, LocalDate start, LocalDate end) where T : notnull
    {
        var now = await review(start, end);
        if (now.TryGetFailure(out var failure)) return Result.Failure<(T, T)>(failure!);
        var before = await review(start.PlusYears(-1), end.PlusYears(-1));
        if (before.TryGetFailure(out failure)) return Result.Failure<(T, T)>(failure!);
        now.TryGetValue(out var n);
        before.TryGetValue(out var b);
        return Result.Success((n!, b!));
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
