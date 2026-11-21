using System.Globalization;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Banking.Application.Spending;

/// <summary>
/// #85: what the scope's bank accounts spent by category, over <paramref name="Month"/> (default: the household's current month) or over
/// [<paramref name="From"/>, <paramref name="To"/>], both given and 24 months at most.
/// </summary>
public sealed record GetSpendingQuery(VisibilityScope Scope, YearMonth? Month, YearMonth? From, YearMonth? To) : IQuery<SpendingView>;

/// <summary>
/// Amounts in <paramref name="Currency"/> (the household's reference), positive for money spent. <paramref name="Incomplete"/>: a line in
/// the range had no rate and is left out. <paramref name="BudgetIncomplete"/>: a charge a budget counts as committed had no rate and is left out. <paramref name="Average"/>: the monthly average of the 12 months before <paramref name="From"/>, or of
/// those the accounts' history covers when fewer (whole months from the first line, and within the household's retention); null when it
/// covers fewer than 3 of them or a line of them has no rate (every average then). <paramref name="From"/> and <paramref name="To"/> are <c>yyyy-MM</c>.
/// <paramref name="Months"/>: every month of the range, oldest first, with what it spent under the same rules as <paramref name="Total"/>.
/// Issue #425, one month only (null over a period): Accueil's figures for that month and scope (<see cref="IMonthBudget"/>):
/// <paramref name="Income"/> (null without a declared source), <paramref name="Committed"/>, <paramref name="Everyday"/> (the month's
/// spending less the lines linked to an item and the tax, so not <paramref name="Total"/>) and <paramref name="Left"/>, income − committed −
/// everyday. <paramref name="LeftEstimate"/>, the current month only: income − committed − the larger of everyday and the usual everyday
/// month (the same lines averaged over the months of <paramref name="Average"/>); null without that average.
/// </summary>
public sealed record SpendingView(
    string Currency, string Total, bool Incomplete, string? Average, IReadOnlyList<SpendingCategoryView> Categories,
    SpendingUncategorisedView Uncategorised, string From, string To, IReadOnlyList<SpendingMonthView> Months, bool BudgetIncomplete,
    string? Income, string? Committed, string? Everyday, string? Left, string? LeftEstimate);

/// <summary>One month of a <see cref="SpendingView"/>: <paramref name="Month"/> is <c>yyyy-MM</c>.</summary>
public sealed record SpendingMonthView(string Month, string Total);

/// <summary>
/// A top-level expense category, its <paramref name="Amount"/> including its children's and the lines filed on it directly (those have no
/// child row of their own); or one of its children, with no children. <paramref name="Budget"/>: only for one month in « Foyer ».
/// </summary>
public sealed record SpendingCategoryView(
    Guid Id, string Name, string Amount, string? Average, IReadOnlyList<SpendingCategoryView> Children, SpendingBudgetView? Budget);

/// <summary>
/// #85 decisions 7 and 8: a category's budget for the month. <paramref name="Planned"/> is the typed amount (a yearly one ÷ 12), or, with
/// <paramref name="Source"/> <c>committed</c>, what the household's expected charges of the month commit there (<paramref name="Committed"/>,
/// null when none). <paramref name="Period"/>: <c>month</c> or <c>year</c>. <paramref name="Left"/> = planned − spent, negative once over.
/// <paramref name="ExpectedSoFar"/>, for the current month only: the committed charges due on or before today, plus what planned leaves
/// over the month's commitment × days elapsed (today included) ÷ days in the month.
/// <paramref name="State"/>: <c>over</c> past the planned amount, <c>ahead</c> (current month) more than 10 % past the expected so far, else
/// <c>on_track</c>; null for a month to come.
/// </summary>
public sealed record SpendingBudgetView(string Planned, string? Committed, string Source, string Period, string Left, string? ExpectedSoFar, string? State);

/// <summary>« Non classées »: the debits of lines with no category; their credits never count. <paramref name="Count"/>: how many of those debits the period has (#426).</summary>
public sealed record SpendingUncategorisedView(string Amount, string? Average, int Count);

/// <summary>
/// #85: the lines behind one figure of <see cref="GetSpendingQuery"/> (same scope, same period): those of the category and its children, or,
/// with <paramref name="Uncategorised"/>, the uncategorised debits; newest first, 50 a page (<paramref name="Page"/> from 1).
/// </summary>
public sealed record ListSpendingLinesQuery(
    VisibilityScope Scope, Guid? CategoryId, bool Uncategorised, YearMonth? Month, YearMonth? From, YearMonth? To, int Page) : IQuery<SpendingLines>;

/// <summary>
/// One page of lines (each in its own currency); <paramref name="Total"/> is the figure the spending view shows for them, in its reference
/// currency, <paramref name="Incomplete"/> as there; <paramref name="Count"/> all the lines, not only the page's; <paramref name="Currency"/> the
/// reference currency of <paramref name="Total"/>.
/// </summary>
public sealed record SpendingLines(IReadOnlyList<BankTransactionView> Items, string Total, int Count, bool Incomplete, string Currency);

[RequestHandler<ListSpendingLinesQuery, SpendingLines>]
public sealed class ListSpendingLinesHandler(
    GetSpendingHandler spending, ITransactionStore lines, ICategoryStore categories, BankLines views)
    : IRequestHandler<ListSpendingLinesQuery, SpendingLines>
{
    public async ValueTask<Result<SpendingLines>> HandleAsync(ListSpendingLinesQuery query, CancellationToken ct = default)
    {
        if (query.Uncategorised == (query.CategoryId is not null))
            return BankingAccess.Fail<SpendingLines>(BankingCodes.SpendingTarget, "Send a categoryId, or uncategorised=true.");
        // The total is the figure itself, so the two never differ: same rows, same conversion, same flag.
        var figures = await spending.SpentAsync(new GetSpendingQuery(query.Scope, query.Month, query.From, query.To), ct);
        if (!figures.TryGetValue(out var sums))
            return BankingAccess.Failed<SpendingLines, GetSpendingHandler.Spent>(figures);

        // An expense category and its children; any other kind is no spending, so it lists nothing.
        List<Guid> ids = query.CategoryId is { } id
            ? [.. (await categories.AllAsync(ct)).Where(c => c.Kind == CategoryKind.Expense && (c.Id == id || c.Parent == id)).Select(c => c.Id)]
            : [];
        List<Guid> keys = query.Uncategorised ? [Guid.Empty] : ids;
        var total = keys.Sum(key => sums.ByCategory.GetValueOrDefault(key).Range);

        var (page, count) = await lines.SpendingLinesAsync(sums.Accounts, ids, query.Uncategorised, sums.From.ToDateInterval().Start, sums.To.ToDateInterval().End,
            (Math.Clamp(query.Page, 1, BankLines.MaxPage) - 1) * BankLines.PageSize, BankLines.PageSize, ct);
        var byId = (await views.ViewsAsync([.. page.Select(r => r.Line).DistinctBy(l => l.Id)], ct)).ToDictionary(v => v.Id);
        // A part's row shows the part's amount, category and note.
        var items = page.Select(r => r.PartId is { } part && byId[r.Line.Id].Parts?.FirstOrDefault(p => p.Id == part) is { } shown
            ? byId[r.Line.Id] with { Amount = shown.Amount, Category = shown.Category, Note = shown.Note, Parts = null, PartId = part }
            : byId[r.Line.Id]).ToList();
        return Result.Success(new SpendingLines(items, GetSpendingHandler.Money(total), count, keys.Any(sums.Incomplete.Contains), sums.Reference.Code));
    }
}

[RequestHandler<GetSpendingQuery, SpendingView>]
public sealed class GetSpendingHandler(
    BankingAccess access, SpendingAccounts scopes, ITransactionStore lines, ICategoryStore categories, IHouseholdSettings household,
    IHouseholdCalendar calendar, IFxRates fx, ICategoryBudgetStore budgets, CommittedByCategory committed, IMonthBudget monthBudget,
    IActorAccessor actor)
    : IRequestHandler<GetSpendingQuery, SpendingView>
{
    public const int MaxMonths = 24;
    private const int AverageMonths = 12;
    internal const int MinHistoryMonths = 3;
    private const decimal AheadMargin = 1.10m;
    internal static readonly StringComparer ByName = StringComparer.Create(CultureInfo.GetCultureInfo("fr-FR"), true);

    /// <summary>
    /// The range's and the preceding <paramref name="Covered"/> months' spending per category (Guid.Empty: none), before any view filter; the
    /// range's per month. <paramref name="Incomplete"/>: the categories (Guid.Empty: none) a line of the range left without a rate.
    /// </summary>
    public sealed record Spent(
        YearMonth From, YearMonth To, IReadOnlyList<Guid> Accounts, Currency Reference, Dictionary<Guid, (decimal Range, decimal Before)> ByCategory,
        Dictionary<YearMonth, decimal> ByMonth, HashSet<Guid> Incomplete, bool AverageMissing, int Covered, LocalDate Today, RateTable Rates, int ToClassify);

    public async ValueTask<Result<Spent>> SpentAsync(GetSpendingQuery query, CancellationToken ct)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<Spent>(enabled);
        var today = await calendar.TodayAsync(ct);
        YearMonth from, to;
        if (query is { From: null, To: null })
            from = to = query.Month ?? today.ToYearMonth();
        else if (query is { Month: null, From: { } f, To: { } t } && Index(f) <= Index(t) && Index(t) - Index(f) < MaxMonths)
            (from, to) = (f, t);
        else
            return Result.Failure<Spent>(new Failure(BankingCodes.SpendingPeriod,
                $"A period names a first and a last month (yyyy-mm), the first not after the last, {MaxMonths} months at most; or send one month.", null));

        var accounts = await scopes.ForAsync(query.Scope, ct);
        var averageFrom = from.PlusMonths(-AverageMonths);
        // Decision 6: the window's whole months from the accounts' first line on, and not before the retention, whose purge keeps linked
        // lines older than it (so the first line can lie). A month is whole when its first day is in.
        static YearMonth Whole(LocalDate day) => day.Day == 1 ? day.ToYearMonth() : day.ToYearMonth().PlusMonths(1);
        var covered = 0;
        if (await lines.EarliestBookedOnAsync(accounts, ct) is { } earliest)
        {
            var start = Whole(earliest);
            if ((await household.ReadAsync(ct)).BankRetentionMonths is { } retention && Whole(today.PlusMonths(-retention)) is var kept && kept > start)
                start = kept;
            covered = Math.Clamp(Index(from) - Index(start), 0, AverageMonths);
        }
        var coveredFrom = from.PlusMonths(-covered);
        var sums = await lines.SpendingAsync(accounts, coveredFrom.ToDateInterval().Start, to.ToDateInterval().End, ct);
        var byId = (await categories.AllAsync(ct)).ToDictionary(c => c.Id);
        var reference = await household.ReferenceCurrencyAsync(ct);
        var foreign = sums.Select(s => s.Currency).Where(c => c != reference).Distinct().ToList();
        // The reference's own quote too: rates are per USD (RateTable converts through it).
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync([.. foreign, reference], averageFrom.ToDateInterval().End, today, ct);

        // Per category (Guid.Empty: none), what the range spent and what its covered months before it spent.
        var spent = new Dictionary<Guid, (decimal Range, decimal Before)>();
        var byMonth = new Dictionary<YearMonth, decimal>();
        var incomplete = new HashSet<Guid>();
        var averageMissing = false;
        foreach (var sum in sums)
        {
            if (Spending(sum, byId) is not { } a || a == 0m)
                continue;
            var inRange = Index(sum.Month) >= Index(from);
            // Decision 5: a past month at its month-end rate; the current one, whose end is after today, at today's.
            if (!rates.TryConvert(a, sum.Currency, reference, sum.Month.ToDateInterval().End, projected: true, out var converted, out _))
            {
                // Never a guess: the line is left out, the range's figure flagged, and no average rather than a short one.
                if (inRange)
                    incomplete.Add(sum.Category ?? Guid.Empty);
                averageMissing |= !inRange;
                continue;
            }
            var key = sum.Category ?? Guid.Empty;
            var (range, before) = spent.GetValueOrDefault(key);
            spent[key] = inRange ? (range + converted, before) : (range, before + converted);
            if (inRange)
                byMonth[sum.Month] = byMonth.GetValueOrDefault(sum.Month) + converted;
        }

        // #426: counted from the same rows as « Non classées », a line without a rate included: it is still to classify.
        var toClassify = sums.Where(s => s.Category is null && Index(s.Month) >= Index(from)).Sum(s => s.DebitLines);
        return Result.Success(new Spent(from, to, accounts, reference, spent, byMonth, incomplete, averageMissing, covered, today, rates, toClassify));
    }

    public async ValueTask<Result<SpendingView>> HandleAsync(GetSpendingQuery query, CancellationToken ct = default)
    {
        var figures = await SpentAsync(query, ct);
        if (!figures.TryGetValue(out var sums))
            return BankingAccess.Failed<SpendingView, Spent>(figures);
        var (from, to, reference, spent, covered, today) = (sums.From, sums.To, sums.Reference, sums.ByCategory, sums.Covered, sums.Today);
        var months = Enumerable.Range(0, Index(to) - Index(from) + 1).Select(i => from.PlusMonths(i))
            .Select(m => new SpendingMonthView(YearMonthPattern.Iso.Format(m), Money(sums.ByMonth.GetValueOrDefault(m)))).ToList();
        var byId = (await categories.AllAsync(ct)).ToDictionary(c => c.Id);
        // Decision 7: the budget is the household's, set against « Foyer » one month at a time.
        var (plans, uncommitted) = query is { Scope: VisibilityScope.Household, From: null, To: null }
            ? await PlansAsync(from, reference, today, byId, ct)
            : ([], false);
        SpendingBudgetView? Budget(Guid id, decimal range) => plans.TryGetValue(id, out var plan) ? Pace(plan, Round(range), from, today) : null;

        var averaged = !sums.AverageMissing && covered >= MinHistoryMonths;
        string? Average(decimal before) => averaged ? Money(before / covered) : null;
        bool Shown((decimal Range, decimal Before) s) => Round(s.Range) != 0m || (averaged && Round(s.Before / covered) != 0m);

        var tops = new List<(SpendingCategoryView View, decimal Range)>();
        foreach (var top in byId.Values.Where(c => c.Parent is null && c.Kind == CategoryKind.Expense))
        {
            var children = byId.Values.Where(c => c.Parent == top.Id).Select(c => (Category: c, Spent: spent.GetValueOrDefault(c.Id))).ToList();
            var own = spent.GetValueOrDefault(top.Id);
            var total = (Range: own.Range + children.Sum(c => c.Spent.Range), Before: own.Before + children.Sum(c => c.Spent.Before));
            if (!Shown(total) && !plans.ContainsKey(top.Id) && !children.Any(c => plans.ContainsKey(c.Category.Id)))
                continue;
            // A budgeted category shows with nothing spent yet.
            var shown = children.Where(c => Shown(c.Spent) || plans.ContainsKey(c.Category.Id))
                .OrderByDescending(c => c.Spent.Range).ThenBy(c => c.Category.Name, ByName)
                .Select(c => new SpendingCategoryView(c.Category.Id, c.Category.Name, Money(c.Spent.Range), Average(c.Spent.Before), [],
                    Budget(c.Category.Id, c.Spent.Range)))
                .ToList();
            tops.Add((new SpendingCategoryView(top.Id, top.Name, Money(total.Range), Average(total.Before), shown, Budget(top.Id, total.Range)),
                total.Range));
        }

        // Issue #425: Accueil's month, one month at a time (a period's would read every module once a month).
        MonthBudget? month = null;
        decimal? estimate = null;
        if (query is { From: null, To: null } && actor.Current?.PersonId is { } reader)
        {
            var read = await monthBudget.MonthAsync(reader, query.Scope, from, today, ct);
            if (!read.TryGetValue(out month))
                return BankingAccess.Failed<SpendingView, MonthBudget>(read);
            // A past month is over: nothing to estimate.
            if (from == today.ToYearMonth() && averaged && month!.Everyday is { } everyday && await UsualEverydayAsync(sums, byId, ct) is { } usual)
                estimate = month.Margin - Math.Max(everyday, usual);
        }

        var none = spent.GetValueOrDefault(Guid.Empty);
        string? Amount(decimal? amount) => amount is { } a ? Money(a) : null;
        return Result.Success(new SpendingView(
            reference.Code, Money(spent.Values.Sum(s => s.Range)), sums.Incomplete.Count > 0, Average(spent.Values.Sum(s => s.Before)),
            [.. tops.OrderByDescending(t => t.Range).ThenBy(t => t.View.Name, ByName).Select(t => t.View)],
            new SpendingUncategorisedView(Money(none.Range), Average(none.Before), sums.ToClassify),
            YearMonthPattern.Iso.Format(from), YearMonthPattern.Iso.Format(to), months, uncommitted,
            Amount(month?.Income), Amount(month?.Committed), Amount(month?.Everyday), Amount(month?.Left), Amount(estimate)));
    }

    /// <summary>
    /// Issue #425: the usual everyday month, Accueil's everyday spending (no line linked to an item, no tax) averaged over the months
    /// <see cref="SpendingView.Average"/> covers, each at its month-end rate; null when a line has none. The Mois habituel itself counts the
    /// linked lines, which the margin has already taken out.
    /// </summary>
    private async Task<decimal?> UsualEverydayAsync(Spent sums, Dictionary<Guid, BankCategory> byId, CancellationToken ct)
    {
        var total = 0m;
        foreach (var sum in await lines.SpendingAsync(sums.Accounts, sums.From.PlusMonths(-sums.Covered).ToDateInterval().Start,
                     sums.From.PlusMonths(-1).ToDateInterval().End, ct, withLinked: false))
        {
            if (DashboardSpending.Everyday(sum, byId) is not { } amount || amount == 0m)
                continue;
            if (!sums.Rates.TryConvert(amount, sum.Currency, sums.Reference, sum.Month.ToDateInterval().End, projected: true, out var converted, out _))
                return null;
            total += converted;
        }
        return total / sums.Covered;
    }

    private sealed record Plan(decimal Planned, decimal? Committed, decimal CommittedToDate, string Source, string Period);

    /// <summary>
    /// Each active expense category's plan for <paramref name="month"/>: its budget's typed amount, else what is committed there; none
    /// without either. One budget per branch, the parent's first (both can exist after an unarchive): a child under a budgeted parent, and
    /// a parent over a budgeted child, have none.
    /// </summary>
    private async Task<(Dictionary<Guid, Plan> Plans, bool Incomplete)> PlansAsync(
        YearMonth month, Currency reference, LocalDate today, Dictionary<Guid, BankCategory> byId, CancellationToken ct)
    {
        static bool Active(BankCategory c) => c is { Kind: CategoryKind.Expense, ArchivedAt: null, MergedInto: null };
        var rows = (await budgets.AllAsync(ct)).Where(b => byId.TryGetValue(b.Category, out var c) && Active(c)).ToDictionary(b => b.Category);
        var (commitments, incomplete) = await committed.ForAsync(month, reference, today, byId, ct);
        var plans = new Dictionary<Guid, Plan>();
        foreach (var category in byId.Values.Where(Active))
        {
            if (category.Parent is { } parent
                    ? rows.ContainsKey(parent)
                    : !rows.ContainsKey(category.Id) && rows.Keys.Any(r => byId[r].Parent == category.Id))
                continue;
            var row = rows.GetValueOrDefault(category.Id);
            var (due, toDate) = commitments.GetValueOrDefault(category.Id);
            var commitment = Round(due);
            decimal? typed = row?.Amount is { } amount ? Round(row.Period == BudgetPeriod.Year ? amount / 12 : amount) : null;
            if (typed is null && commitment <= 0m)
                continue;
            plans[category.Id] = new Plan(typed ?? commitment, commitment > 0m ? commitment : null, Round(toDate), typed is null ? "committed" : "typed",
                // « Annuel » describes a typed yearly amount; a commitment is the month's.
                typed is not null && row!.Period == BudgetPeriod.Year ? "year" : "month");
        }
        return (plans, incomplete);
    }

    /// <summary>
    /// Decision 8: the pace of the current month: a committed charge counts whole from its due day, the rest of planned by the days
    /// elapsed; a past month's spent against planned only.
    /// </summary>
    private static SpendingBudgetView Pace(Plan plan, decimal spent, YearMonth month, LocalDate today)
    {
        var current = month == today.ToYearMonth();
        var spread = Math.Max(plan.Planned - (plan.Committed ?? 0m), 0m);
        decimal? expected = current ? Round(plan.CommittedToDate + spread * today.Day / month.ToDateInterval().Length) : null;
        var state = month > today.ToYearMonth() ? null
            : spent > plan.Planned ? "over"
            : expected is { } soFar && spent > soFar * AheadMargin ? "ahead"
            : "on_track";
        return new SpendingBudgetView(Money(plan.Planned), plan.Committed is { } c ? Money(c) : null, plan.Source, plan.Period,
            Money(plan.Planned - spent), expected is { } e ? Money(e) : null, state);
    }

    /// <summary>
    /// Decision 3: what <paramref name="sum"/> spent, positive for money out; null when it is no spending. An expense category nets its
    /// refunds, a line with no category counts its debits only, income and transfer never. Categories are never deleted (a merge moves
    /// the lines), so an unknown id is not expected; it would not count.
    /// </summary>
    internal static decimal? Spending(SpendingSum sum, IReadOnlyDictionary<Guid, BankCategory> byId)
        => sum.Category is not { } id ? -sum.Debits
            : byId.TryGetValue(id, out var category) && category.Kind == CategoryKind.Expense ? -(sum.Debits + sum.Credits)
            : null;

    private static int Index(YearMonth month) => month.Year * 12 + month.Month;

    private static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    internal static string Money(decimal amount) => Round(amount).ToString("0.00", CultureInfo.InvariantCulture);
}
