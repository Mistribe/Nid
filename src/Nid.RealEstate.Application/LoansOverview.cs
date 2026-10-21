using System.Globalization;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.RealEstate.Application.LoanReading;

namespace Nid.RealEstate.Application;

/// <summary>Loans dashboard spec §3.2: the household's loans at a glance, in the Foyer / Moi scope (D6).</summary>
public sealed record GetLoansOverviewQuery(VisibilityScope Scope) : IQuery<LoansOverview>;

/// <summary>An amount of the household in the reference currency, as the other modules' overviews send it.</summary>
public sealed record LoanMoneyView(string Amount, string Currency);

/// <summary>D4: a figure the overview could not give (<c>core.fx_rate_missing</c>), with what it lacked.</summary>
public sealed record LoanFindingView(string Code, string Message, string? Reference);

/// <summary>
/// A running loan (spec §3.2): its own amounts in its currency, today's balance and this month's payment (D3, null without a
/// payment this month) converted too. <c>PropertyName</c> is null when the reader cannot read the property, as in the list.
/// </summary>
public sealed record LoanOverviewRow(
    ItemId Id, string Name, string? Lender, ItemId? PropertyId, string? PropertyName, IReadOnlyList<ItemOwnerView> Owners,
    string Currency, string Principal, string Balance, string RepaidShare, string? MonthlyPayment, string? CurrentRate,
    LocalDate? EndDate, LoanMoneyView? BalanceConverted, LoanMoneyView? MonthlyPaymentConverted);

public sealed record LoanBalanceView(ItemId LoanId, LoanMoneyView? Balance);

/// <summary>One month of the curve (D8): each loan still running that month, and their sum.</summary>
public sealed record BalanceMonthView(string Month, LoanMoneyView? Total, IReadOnlyList<LoanBalanceView> ByLoan);

/// <summary>A change ahead: a loan's last payment or a phase's end, and the household's payment the month after it (D3).</summary>
public sealed record LoanEventView(LocalDate Date, string Code, ItemId LoanId, int? PhaseSeq, LoanMoneyView? MonthlyPaymentAfter);

/// <summary>
/// Spec §3.2. Every household figure is in <c>Currency</c>, null when a rate is missing (then a finding names it); a future point
/// of a loan in another currency is converted at today's rate and flags <c>ConvertedIsEstimate</c> (D4).
/// </summary>
public sealed record LoansOverview(
    string Currency, bool ConvertedIsEstimate, int RunningCount, int EndedCount, LoanMoneyView? Balance, LoanMoneyView? Principal,
    LoanMoneyView? PrincipalRepaid, string? RepaidShare, LoanMoneyView? MonthlyPayment, LoanMoneyView? CostPaid,
    LoanMoneyView? CostRemaining, LocalDate? FreeOn, LocalDate? NextPaymentOn, LoanMoneyView? NextPaymentTotal,
    IReadOnlyList<LoanOverviewRow> Loans, IReadOnlyList<BalanceMonthView> Months, IReadOnlyList<LoanEventView> Events,
    IReadOnlyList<LoanFindingView> Findings) : IExposesItems
{
    // The months and events name the same loans as the rows.
    public IEnumerable<ItemId> ExposedItems() => Loans.Select(l => l.Id);
}

public static class LoanOverviewCodes
{
    public const string LoanEnds = "realestate.loan_ends";
    public const string PhaseEnds = "realestate.phase_ends";
}

/// <summary>A visible loan as the overview reads it; <c>PropertyName</c> as <see cref="LoanSummary"/>'s.</summary>
internal sealed record OverviewLoan(ItemAccess Item, StoredLoan Loan, string? PropertyName);

[RequestHandler<GetLoansOverviewQuery, LoansOverview>]
public sealed class GetLoansOverviewHandler(
    IVisibleItems visible, IItemRegistry items, ILoanStore loans, IHouseholdCalendar calendar, IAccessPolicy policy, IActorAccessor actor,
    IHouseholdSettings household, IFxRates fx)
    : IRequestHandler<GetLoansOverviewQuery, LoansOverview>
{
    public async ValueTask<Result<LoansOverview>> HandleAsync(GetLoansOverviewQuery query, CancellationToken ct = default)
    {
        var person = actor.Current!.PersonId!.Value;
        var ids = await visible.ForAsync(person, query.Scope, ct);
        var today = await calendar.TodayAsync(ct);

        // Batched reads spec §3, as the list: the items and the readable properties' names, each read once.
        var found = await loans.FindManyAsync(ids.ToList(), ct);
        var accesses = await items.FindManyAsync(found.Select(l => l.ItemId).ToList(), ct);
        var readable = await policy.CheckManyAsync(person, found.Select(l => l.PropertyId).OfType<ItemId>().ToList(), AccessKind.Read, ct);
        var properties = await items.FindManyAsync(readable.ToList(), ct);
        var set = found
            .Where(l => accesses.ContainsKey(l.ItemId))
            .Select(l => new OverviewLoan(accesses[l.ItemId], l,
                l.PropertyId is { } property && properties.TryGetValue(property, out var linked) ? linked.DisplayName : null))
            .ToList();

        var reference = await household.ReferenceCurrencyAsync(ct);
        var foreign = set.Select(l => LoanViews.CurrencyOf(l.Item)).Where(c => c != reference).Distinct().ToList();
        // The reference's own quote is needed too: rates are per USD. D4: every figure at today's rate.
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync([.. foreign, reference], today, today, ct);
        return Result.Success(LoansOverviewMath.Build(set, reference, rates, today));
    }
}

/// <summary>Spec §3.2's figures, pure: tested with the real engine in Nid.RealEstate.Tests.</summary>
internal static class LoansOverviewMath
{
    private static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    /// <summary>A loan read once: its schedule (D2), its figures today, and its payment of each month (D3: the first one due in it).</summary>
    private sealed class Reading
    {
        public Reading(OverviewLoan source, LocalDate today)
        {
            Source = source;
            Currency = LoanViews.CurrencyOf(source.Item);
            Terms = LoanReading.Terms(Currency, source.Loan.Contract, source.Loan.Fees, source.Loan.Repayments);
            Schedule = LoanReading.Schedule(Terms, source.Loan);
            Figures = LoanMath.At(Terms, Schedule, today);
            Ended = LoanReading.Ended(Terms, Schedule, today);
            foreach (var row in Schedule.Rows)
                Payments.TryAdd(row.DueDate.ToYearMonth(), row.Payment + row.Insurance);
        }

        public OverviewLoan Source { get; }
        public Currency Currency { get; }
        public LoanTerms Terms { get; }
        public LoanSchedule Schedule { get; }
        public LoanFigures Figures { get; }
        public bool Ended { get; }
        public Dictionary<YearMonth, decimal> Payments { get; } = [];

        /// <summary>The month of its last payment; today's for a loan without one (never stored: a loan has at least one payment).</summary>
        public YearMonth LastMonth(LocalDate today) => (Figures.EndDate ?? today).ToYearMonth();
    }

    /// <summary>Each amount converted alone and rounded to the cent, so a total is the sum of the lines shown.</summary>
    private sealed class Converter(Currency reference, RateTable rates, LocalDate today)
    {
        private readonly SortedDictionary<string, LoanFindingView> missing = new(StringComparer.Ordinal);

        public IReadOnlyList<LoanFindingView> Missing => missing.Values.ToList();

        public decimal? Of(decimal amount, Currency from)
        {
            if (from == reference || amount == 0m) return amount; // zero converts to zero at any rate
            if (rates.TryConvert(amount, from, reference, today, projected: false, out var converted, out var miss))
                return Math.Round(converted, 2, MidpointRounding.AwayFromZero);
            var key = $"{miss.Currency.Code}:{LocalDatePattern.Iso.Format(miss.Date)}";
            missing.TryAdd(key, new LoanFindingView(FxCodes.RateMissing,
                $"No {miss.Currency.Code} rate within {rates.MaxGapDays} days before {LocalDatePattern.Iso.Format(miss.Date)}.", key));
            return null;
        }

        public LoanMoneyView? View(decimal? amount) => amount is { } a ? new LoanMoneyView(Amount(a), reference.Code) : null;
    }

    public static LoansOverview Build(IReadOnlyList<OverviewLoan> set, Currency reference, RateTable rates, LocalDate today)
    {
        var convert = new Converter(reference, rates, today);
        var read = set.Select(l => new Reading(l, today)).ToList();
        // D7: an ended loan is in no figure; spec §3.2: the rows by balance, largest first (converted, an unknown one last).
        var running = read.Where(r => !r.Ended)
            .Select(r => (Reading: r, Balance: convert.Of(r.Figures.Balance, r.Currency)))
            .OrderBy(x => x.Balance is null).ThenByDescending(x => x.Balance)
            .ThenBy(x => x.Reading.Source.Item.DisplayName, French).ThenBy(x => x.Reading.Source.Item.Id.Value)
            .Select(x => x.Reading)
            .ToList();

        decimal? Sum(IEnumerable<decimal?> amounts)
        {
            var total = 0m;
            foreach (var a in amounts)
            {
                if (a is not { } value) return null;
                total += value;
            }
            return total;
        }
        decimal? Household(Func<Reading, decimal> amount) => Sum(running.Select(r => convert.Of(amount(r), r.Currency)));
        decimal? PaymentOf(YearMonth month) => Household(r => r.Payments.GetValueOrDefault(month));

        var thisMonth = today.ToYearMonth();
        var rows = running.Select(r =>
        {
            var f = r.Figures;
            var payment = r.Payments.TryGetValue(thisMonth, out var due) ? due : (decimal?)null;
            return new LoanOverviewRow(r.Source.Item.Id, r.Source.Item.DisplayName, r.Source.Loan.Lender, r.Source.Loan.PropertyId,
                r.Source.PropertyName, r.Source.Item.OwnerViews(), r.Currency.Code, Amount(r.Terms.Principal), Amount(f.Balance),
                Fraction(f.PrincipalRepaid / r.Terms.Principal), Amount(payment), Rate(f.CurrentRate), f.EndDate,
                convert.View(convert.Of(f.Balance, r.Currency)), payment is { } p ? convert.View(convert.Of(p, r.Currency)) : null);
        }).ToList();

        var principal = Household(r => r.Terms.Principal);
        var repaid = Household(r => r.Figures.PrincipalRepaid);
        var freeOn = running.Select(r => r.Figures.EndDate).Max();
        var nextOn = running.Select(r => r.Figures.NextPaymentOn).Min();
        var nextTotal = nextOn is { } next
            ? Sum(running.Where(r => r.Figures.NextPaymentOn == next).Select(r => convert.Of(r.Figures.NextPaymentTotal ?? 0m, r.Currency)))
            : null;

        var months = Months(running, reference, today, freeOn, convert, out var estimate);
        var events = running
            .SelectMany((r, rank) => LoanReading.Changes(r.Schedule.Rows)
                .Where(c => c.On >= today && c.On <= freeOn)
                .Select(c => (Change: c, Loan: r.Source.Item.Id, Rank: rank)))
            .OrderBy(x => x.Change.On).ThenBy(x => x.Rank).ThenBy(x => x.Change.EndingPhase is null)
            .Select(x => new LoanEventView(x.Change.On, x.Change.EndingPhase is null ? LoanOverviewCodes.LoanEnds : LoanOverviewCodes.PhaseEnds,
                x.Loan, x.Change.EndingPhase, convert.View(PaymentOf(x.Change.On.ToYearMonth().PlusMonths(1)))))
            .ToList();

        return new LoansOverview(reference.Code, estimate, running.Count, read.Count - running.Count,
            convert.View(Household(r => r.Figures.Balance)), convert.View(principal), convert.View(repaid),
            principal is { } of && of != 0m && repaid is { } done ? Fraction(done / of) : null,
            convert.View(PaymentOf(thisMonth)), convert.View(Household(r => r.Figures.CostPaid)),
            convert.View(Household(r => r.Figures.CostRemaining)), freeOn, nextOn, convert.View(nextTotal),
            rows, months, events, convert.Missing);
    }

    /// <summary>
    /// D8 and spec §3.2: one point a month, from this month to the month of the last payment. This month's point is today's
    /// balance; a later month's is the balance after the last payment due by its last day (an early repayment applied after
    /// that payment included), the principal before the first payment. A loan settled before a month is not in it.
    /// </summary>
    private static List<BalanceMonthView> Months(
        IReadOnlyList<Reading> running, Currency reference, LocalDate today, LocalDate? freeOn, Converter convert, out bool estimate)
    {
        estimate = false;
        if (freeOn is not { } last) return [];
        var thisMonth = today.ToYearMonth();
        var balances = running.Select(r => Balances(r, thisMonth, r.LastMonth(today))).ToList();
        var months = new List<BalanceMonthView>();
        var i = 0;
        for (var month = thisMonth; month <= last.ToYearMonth(); month = month.PlusMonths(1), i++)
        {
            var byLoan = new List<LoanBalanceView>();
            decimal? total = 0m;
            for (var k = 0; k < running.Count; k++)
            {
                if (i >= balances[k].Count) continue; // settled before this month
                var r = running[k];
                var converted = convert.Of(balances[k][i], r.Currency);
                // D4: no future rate exists, so a later month of a loan in another currency is an estimate.
                estimate |= i > 0 && r.Currency != reference;
                byLoan.Add(new LoanBalanceView(r.Source.Item.Id, convert.View(converted)));
                total = total is { } t && converted is { } c ? t + c : null;
            }
            months.Add(new BalanceMonthView(month.ToString("yyyy-MM", CultureInfo.InvariantCulture), convert.View(total), byLoan));
        }
        return months;
    }

    /// <summary>A loan's balance at each month of <paramref name="from"/>..<paramref name="to"/>, the rows walked once.</summary>
    private static List<decimal> Balances(Reading r, YearMonth from, YearMonth to)
    {
        var rows = r.Schedule.Rows;
        var balances = new List<decimal> { r.Figures.Balance };
        var next = 0;
        ScheduleRow? paid = null;
        for (var month = from.PlusMonths(1); month <= to; month = month.PlusMonths(1))
        {
            var end = month.ToDateInterval().End;
            while (next < rows.Count && rows[next].DueDate <= end)
                paid = rows[next++];
            balances.Add(paid?.Balance ?? LoanMath.At(r.Terms, r.Schedule, end).Balance);
        }
        return balances;
    }

    /// <summary>Spec §3.2: a share as a fraction of four decimals, to the nearest; the screen writes it as a percent.</summary>
    private static string Fraction(decimal x) => Math.Round(x, 4, MidpointRounding.AwayFromZero).ToString("0.0000", CultureInfo.InvariantCulture);
}
