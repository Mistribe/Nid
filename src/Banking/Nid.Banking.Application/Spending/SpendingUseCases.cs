using System.Globalization;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
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
/// the range had no rate and is left out. <paramref name="Average"/>: the monthly average of the 12 months before <paramref name="From"/>,
/// null when the accounts' history covers fewer than 3 of them. <paramref name="From"/> and <paramref name="To"/> are <c>yyyy-MM</c>.
/// </summary>
public sealed record SpendingView(
    string Currency, string Total, bool Incomplete, string? Average, IReadOnlyList<SpendingCategoryView> Categories,
    SpendingUncategorisedView Uncategorised, string From, string To);

/// <summary>
/// A top-level expense category, its <paramref name="Amount"/> including its children's and the lines filed on it directly (those have no
/// child row of their own); or one of its children, with no children.
/// </summary>
public sealed record SpendingCategoryView(Guid Id, string Name, string Amount, string? Average, IReadOnlyList<SpendingCategoryView> Children);

/// <summary>« Non classées »: the debits of lines with no category; their credits never count.</summary>
public sealed record SpendingUncategorisedView(string Amount, string? Average);

[RequestHandler<GetSpendingQuery, SpendingView>]
public sealed class GetSpendingHandler(
    BankingAccess access, SpendingAccounts scopes, ITransactionStore lines, ICategoryStore categories, IHouseholdSettings household,
    IHouseholdCalendar calendar, IFxRates fx) : IRequestHandler<GetSpendingQuery, SpendingView>
{
    public const int MaxMonths = 24;
    private const int AverageMonths = 12;
    private const int MinHistoryMonths = 3;
    private static readonly StringComparer ByName = StringComparer.Create(CultureInfo.GetCultureInfo("fr-FR"), true);

    public async ValueTask<Result<SpendingView>> HandleAsync(GetSpendingQuery query, CancellationToken ct = default)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<SpendingView>(enabled);
        var today = await calendar.TodayAsync(ct);
        YearMonth from, to;
        if (query is { From: null, To: null })
            from = to = query.Month ?? today.ToYearMonth();
        else if (query is { Month: null, From: { } f, To: { } t } && Index(f) <= Index(t) && Index(t) - Index(f) < MaxMonths)
            (from, to) = (f, t);
        else
            return Result.Failure<SpendingView>(new Failure(BankingCodes.SpendingPeriod,
                $"A period names a first and a last month (yyyy-mm), the first not after the last, {MaxMonths} months at most; or send one month.", null));

        var accounts = await scopes.ForAsync(query.Scope, ct);
        var averageFrom = from.PlusMonths(-AverageMonths);
        var sums = await lines.SpendingAsync(accounts, averageFrom.ToDateInterval().Start, to.ToDateInterval().End, ct);
        var byId = (await categories.AllAsync(ct)).ToDictionary(c => c.Id);
        var reference = await household.ReferenceCurrencyAsync(ct);
        var foreign = sums.Select(s => s.Currency).Where(c => c != reference).Distinct().ToList();
        // The reference's own quote too: rates are per USD (RateTable converts through it).
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync([.. foreign, reference], averageFrom.ToDateInterval().End, today, ct);

        // Per category (Guid.Empty: none), what the range spent and what the 12 months before it spent.
        var spent = new Dictionary<Guid, (decimal Range, decimal Before)>();
        var incomplete = false;
        foreach (var sum in sums)
        {
            // Decision 3: an expense category nets its refunds, a line with no category counts its debits only, income and transfer never.
            decimal? amount = sum.Category is not { } id ? -sum.Debits
                : byId.TryGetValue(id, out var category) && category.Kind == CategoryKind.Expense ? -(sum.Debits + sum.Credits)
                : null;
            if (amount is not { } a || a == 0m)
                continue;
            var inRange = Index(sum.Month) >= Index(from);
            // Decision 5: a past month at its month-end rate; the current one, whose end is after today, at today's.
            if (!rates.TryConvert(a, sum.Currency, reference, sum.Month.ToDateInterval().End, projected: true, out var converted, out _))
            {
                // Never a guess: the line is left out, the range's figure flagged.
                incomplete |= inRange;
                continue;
            }
            var key = sum.Category ?? Guid.Empty;
            var (range, before) = spent.GetValueOrDefault(key);
            spent[key] = inRange ? (range + converted, before) : (range, before + converted);
        }

        // Decision 6: months of the window from the accounts' first line on.
        var earliest = await lines.EarliestBookedOnAsync(accounts, ct);
        var averaged = earliest is { } e && Math.Clamp(Index(from) - Index(e.ToYearMonth()), 0, AverageMonths) >= MinHistoryMonths;
        string? Average(decimal before) => averaged ? Money(before / AverageMonths) : null;
        bool Shown((decimal Range, decimal Before) s) => Round(s.Range) != 0m || (averaged && Round(s.Before / AverageMonths) != 0m);

        var tops = new List<(SpendingCategoryView View, decimal Range)>();
        foreach (var top in byId.Values.Where(c => c.Parent is null && c.Kind == CategoryKind.Expense))
        {
            var children = byId.Values.Where(c => c.Parent == top.Id).Select(c => (Category: c, Spent: spent.GetValueOrDefault(c.Id))).ToList();
            var own = spent.GetValueOrDefault(top.Id);
            var total = (Range: own.Range + children.Sum(c => c.Spent.Range), Before: own.Before + children.Sum(c => c.Spent.Before));
            if (!Shown(total))
                continue;
            var shown = children.Where(c => Shown(c.Spent)).OrderByDescending(c => c.Spent.Range).ThenBy(c => c.Category.Name, ByName)
                .Select(c => new SpendingCategoryView(c.Category.Id, c.Category.Name, Money(c.Spent.Range), Average(c.Spent.Before), []))
                .ToList();
            tops.Add((new SpendingCategoryView(top.Id, top.Name, Money(total.Range), Average(total.Before), shown), total.Range));
        }

        var none = spent.GetValueOrDefault(Guid.Empty);
        return Result.Success(new SpendingView(
            reference.Code, Money(spent.Values.Sum(s => s.Range)), incomplete, Average(spent.Values.Sum(s => s.Before)),
            [.. tops.OrderByDescending(t => t.Range).ThenBy(t => t.View.Name, ByName).Select(t => t.View)],
            new SpendingUncategorisedView(Money(none.Range), Average(none.Before)),
            YearMonthPattern.Iso.Format(from), YearMonthPattern.Iso.Format(to)));
    }

    private static int Index(YearMonth month) => month.Year * 12 + month.Month;

    private static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    private static string Money(decimal amount) => Round(amount).ToString("0.00", CultureInfo.InvariantCulture);
}
