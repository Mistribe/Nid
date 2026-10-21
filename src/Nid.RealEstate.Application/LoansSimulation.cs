using System.Globalization;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.RealEstate.Application.LoanReading;

namespace Nid.RealEstate.Application;

/// <summary>D11: a penalty typed for one loan of the split, never computed.</summary>
public sealed record LoanPenalty(ItemId LoanId, decimal Amount);

/// <summary>
/// Loans dashboard spec §5: an early repayment of <paramref name="Amount"/> (reference currency) on <paramref name="On"/>, split
/// across the running loans by <paramref name="Split"/>; stores nothing and needs read access only (D14).
/// </summary>
public sealed record SimulateLoansQuery(
    VisibilityScope Scope, LocalDate On, decimal Amount, SplitRule Split, ItemId? LoanId, RepaymentEffect Effect, IReadOnlyList<LoanPenalty> Penalties)
    : IQuery<LoansSimulation>;

public sealed record LoansSimulationAsked(LocalDate On, string Amount, string Split, ItemId? LoanId, string Effect);

/// <summary>
/// A running loan in the simulation: its part (« 0 » when it takes nothing), its penalty, before and after. <c>Code</c> is
/// <c>realestate.other_currency</c> for a loan outside the reference currency (D12), which takes nothing.
/// </summary>
public sealed record SimulatedLoanView(
    ItemId LoanId, string? Code, string? Rate, string Amount, string Penalty, bool Settled, LocalDate? EndBefore, LocalDate? EndAfter,
    string? PaymentBefore, string? PaymentAfter, string InterestSaved, string NetSaving, bool CanWrite);

/// <summary>The household's remaining balance at a month, without and with the repayment (the overview's rule of a point).</summary>
public sealed record SimulationMonthView(string Month, LoanMoneyView? Before, LoanMoneyView? After);

/// <summary>Spec §5.2: the household's result; the split loans' amounts are in the reference currency (D12).</summary>
public sealed record LoansSimulation(
    string Currency, LoansSimulationAsked Asked, IReadOnlyList<SimulatedLoanView> Loans, LoanMoneyView InterestSaved, LoanMoneyView Penalties,
    LoanMoneyView NetSaving, LoanMoneyView? MonthlyPaymentBefore, LoanMoneyView? MonthlyPaymentAfter, string PaymentMonth,
    LocalDate? FreeOnBefore, LocalDate? FreeOnAfter, IReadOnlyList<SimulationMonthView> Months, IReadOnlyList<LoanFindingView> Findings)
    : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Loans.Select(l => l.LoanId);
}

public static class LoanSimulationCodes
{
    public const string OtherCurrency = "realestate.other_currency";
    public const string PenaltyWithoutRepayment = "realestate.penalty_without_repayment";
}

[RequestHandler<SimulateLoansQuery, LoansSimulation>]
public sealed class SimulateLoansHandler(
    IVisibleItems visible, IItemRegistry items, ILoanStore loans, IHouseholdCalendar calendar, IAccessPolicy policy, IActorAccessor actor,
    IHouseholdSettings household, IFxRates fx)
    : IRequestHandler<SimulateLoansQuery, LoansSimulation>
{
    public async ValueTask<Result<LoansSimulation>> HandleAsync(SimulateLoansQuery query, CancellationToken ct = default)
    {
        if (LoanRules.Amount(query.Amount, "amount") is { } refused)
            return Result.Failure<LoansSimulation>(refused);
        for (var i = 0; i < query.Penalties.Count; i++)
            if (LoanRules.Amount(query.Penalties[i].Amount, $"penalties[{i}].amount") is { } wrong)
                return Result.Failure<LoansSimulation>(wrong);

        var person = actor.Current!.PersonId!.Value;
        var ids = await visible.ForAsync(person, query.Scope, ct);
        var today = await calendar.TodayAsync(ct);
        // Batched reads spec §3, as the overview: the items and the write checks, each read once.
        var found = await loans.FindManyAsync(ids.ToList(), ct);
        var accesses = await items.FindManyAsync(found.Select(l => l.ItemId).ToList(), ct);
        var writable = await policy.CheckManyAsync(person, found.Select(l => l.ItemId).ToList(), AccessKind.Write, ct);
        var set = found.Where(l => accesses.ContainsKey(l.ItemId)).Select(l => new OverviewLoan(accesses[l.ItemId], l, null)).ToList();

        var reference = await household.ReferenceCurrencyAsync(ct);
        var foreign = set.Select(l => LoanViews.CurrencyOf(l.Item)).Where(c => c != reference).Distinct().ToList();
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync([.. foreign, reference], today, today, ct);
        return LoansOverviewMath.Simulate(set, reference, rates, today, query, writable);
    }
}

internal static partial class LoansOverviewMath
{
    private static Failure Refuse(string field, string message, string code = LoanCodes.InvalidLoan)
        => new(code, message, new Dictionary<string, object?> { ["field"] = field });

    /// <summary>One split loan: its schedules without and with its part, read as its readers read it (D2).</summary>
    private sealed record Simulated(Reading Before, Reading After, decimal Part, decimal Penalty, bool Settled);

    /// <summary>
    /// Spec §5: the split (D9) over the running loans in the reference currency (D12), each part added to its loan as an early
    /// repayment and read again — the bank's table merged with it when the bank is the reference, as a stored repayment would
    /// be; when the table no longer fits it, both sides read the calculation — then the household's figures without and with.
    /// </summary>
    public static Result<LoansSimulation> Simulate(
        IReadOnlyList<OverviewLoan> set, Currency reference, RateTable rates, LocalDate today, SimulateLoansQuery query, IReadOnlySet<ItemId> writable)
    {
        var running = set.Select(l => new Reading(l, today)).Where(r => !r.Ended).ToList();
        var freeOn = running.Select(r => r.Figures.EndDate).Max();
        if (query.On < today)
            return Result.Failure<LoansSimulation>(Refuse("on", "An early repayment is simulated today or later."));
        if (freeOn is not { } last || query.On > last)
            return Result.Failure<LoansSimulation>(Refuse("on", "An early repayment is simulated at the latest on the household's last payment."));

        var eligible = running.Where(r => r.Currency == reference).ToList();
        if (query.Split == SplitRule.Chosen && !eligible.Any(r => r.Source.Item.Id == query.LoanId))
            return Result.Failure<LoansSimulation>(Refuse("loanId", "The chosen loan is not a running loan of the household in its reference currency."));
        var at = eligible.ToDictionary(r => r.Source.Item.Id.Value, r => LoanMath.At(r.Terms, r.Schedule, query.On));
        var candidates = eligible.Select(r => new SplitCandidate(r.Source.Item.Id.Value, at[r.Source.Item.Id.Value].CurrentRate ?? 0m,
            at[r.Source.Item.Id.Value].Balance)).ToList();
        var ceiling = query.Split == SplitRule.Chosen ? at[query.LoanId!.Value.Value].Balance : candidates.Sum(c => c.Balance);
        if (query.Amount > ceiling)
            return Result.Failure<LoansSimulation>(Refuse("amount", "The amount is above what is left to repay on the loans it is split across.", LoanCodes.EventAboveBalance));
        var parts = LoanSplit.Split(candidates, query.Amount, query.Split, query.LoanId?.Value);
        var partOf = parts.ToDictionary(p => p.LoanId);

        var penalties = new Dictionary<Guid, decimal>();
        for (var i = 0; i < query.Penalties.Count; i++)
        {
            var p = query.Penalties[i];
            if (!partOf.TryGetValue(p.LoanId.Value, out var part) || part.Amount == 0m || !penalties.TryAdd(p.LoanId.Value, p.Amount))
                return Result.Failure<LoansSimulation>(Refuse($"penalties[{i}].loanId", "A penalty is for a loan that takes a part, once.",
                    LoanSimulationCodes.PenaltyWithoutRepayment));
        }

        var simulated = new Dictionary<Guid, Simulated>();
        foreach (var r in eligible)
        {
            var part = partOf[r.Source.Item.Id.Value];
            if (part.Amount == 0m) continue;
            var after = WithRepayment(r, new EarlyRepayment(query.On, part.Amount, query.Effect), today);
            if (after.TryGetFailure(out var failure))
                return Result.Failure<LoansSimulation>(failure!);
            after.TryGetValue(out var pair);
            simulated[r.Source.Item.Id.Value] = new Simulated(pair.Before, pair.After, part.Amount, penalties.GetValueOrDefault(r.Source.Item.Id.Value), part.Settles);
        }

        var convert = new Converter(reference, rates, today);
        var paymentMonth = query.On.ToYearMonth().PlusMonths(1);
        var before = running.Select(r => simulated.TryGetValue(r.Source.Item.Id.Value, out var s) ? s.Before : r).ToList();
        var afterAll = running.Select(r => simulated.TryGetValue(r.Source.Item.Id.Value, out var s) ? s.After : r).ToList();
        decimal? PaymentOf(IReadOnlyList<Reading> readings) => SumOrNull(readings.Select(r => convert.Of(r.Payments.GetValueOrDefault(paymentMonth), r.Currency)));
        var freeBefore = before.Select(r => r.Figures.EndDate).Max();
        var freeAfter = afterAll.Select(r => r.Figures.EndDate).Max();
        var monthsBefore = Months(before, reference, today, freeBefore, convert, out _);
        var monthsAfter = Months(afterAll, reference, today, freeBefore, convert, out _);

        static decimal Interest(Reading r) => r.Schedule.Rows.Sum(x => x.Interest);
        LocalDate? EndOf(Reading r) => r.Schedule.Rows.LastOrDefault()?.DueDate;
        decimal? PaymentAfterOn(Reading r) => r.Schedule.Rows.FirstOrDefault(x => x.DueDate > query.On) is { } row ? row.Payment + row.Insurance : null;

        SimulatedLoanView View(Reading r)
        {
            var id = r.Source.Item.Id;
            var canWrite = writable.Contains(id);
            if (r.Currency != reference)
                return new SimulatedLoanView(id, LoanSimulationCodes.OtherCurrency, Rate(LoanMath.At(r.Terms, r.Schedule, query.On).CurrentRate), "0.00",
                    "0.00", false, EndOf(r), EndOf(r), Amount(PaymentAfterOn(r)), Amount(PaymentAfterOn(r)), "0.00", "0.00", canWrite);
            var rate = Rate(at[id.Value].CurrentRate);
            if (!simulated.TryGetValue(id.Value, out var s))
                return new SimulatedLoanView(id, null, rate, "0.00", "0.00", false, EndOf(r), EndOf(r), Amount(PaymentAfterOn(r)),
                    Amount(PaymentAfterOn(r)), "0.00", "0.00", canWrite);
            var saved = Interest(s.Before) - Interest(s.After);
            return new SimulatedLoanView(id, null, rate, Amount(s.Part), Amount(s.Penalty), s.Settled, EndOf(s.Before), EndOf(s.After),
                Amount(PaymentAfterOn(s.Before)), Amount(PaymentAfterOn(s.After)), Amount(saved), Amount(saved - s.Penalty), canWrite);
        }

        // Spec §5.2: every running loan, in the split's order, then those that take nothing, then the other currencies.
        var order = parts.Select((p, i) => (p.LoanId, Rank: p.Amount > 0m ? i : parts.Count + i)).ToDictionary(x => x.LoanId, x => x.Rank);
        var views = running.OrderBy(r => order.TryGetValue(r.Source.Item.Id.Value, out var rank) ? rank : int.MaxValue)
            .ThenBy(r => r.Source.Item.DisplayName, French).ThenBy(r => r.Source.Item.Id.Value)
            .Select(View).ToList();
        var interestSaved = simulated.Values.Sum(s => Interest(s.Before) - Interest(s.After));
        var penaltyTotal = simulated.Values.Sum(s => s.Penalty);
        LoanMoneyView Money(decimal amount) => new(Amount(amount), reference.Code);

        return Result.Success(new LoansSimulation(reference.Code,
            new LoansSimulationAsked(query.On, Amount(query.Amount), LoanWire.Of(query.Split), query.Split == SplitRule.Chosen ? query.LoanId : null,
                LoanWire.Of(query.Effect)),
            views, Money(interestSaved), Money(penaltyTotal), Money(interestSaved - penaltyTotal),
            convert.View(PaymentOf(before)), convert.View(PaymentOf(afterAll)), paymentMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            freeBefore, freeAfter,
            monthsBefore.Zip(monthsAfter, (b, a) => new SimulationMonthView(b.Month, b.Total, a.Total)).ToList(),
            convert.Missing));
    }

    /// <summary>
    /// D10: the loan with its part as one more early repayment, through the same engine and the same reading as a stored one.
    /// A refusal of the engine answers at <c>amount</c> or <c>on</c>, the simulation's own fields.
    /// </summary>
    private static Result<(Reading Before, Reading After)> WithRepayment(Reading r, EarlyRepayment repayment, LocalDate today)
    {
        var terms = r.Terms with { Events = [.. r.Terms.Events.Where(e => e.OnDate <= repayment.OnDate), repayment, .. r.Terms.Events.Where(e => e.OnDate > repayment.OnDate)] };
        var built = LoanScheduleBuilder.Build(terms);
        if (built.TryGetFailure(out var failure))
            return Result.Failure<(Reading, Reading)>(failure!.Code == LoanCodes.EventAboveBalance
                ? Refuse("amount", failure.Message, LoanCodes.EventAboveBalance)
                : Refuse("on", failure.Message, failure.Code));
        built.TryGetValue(out var computed);
        var with = r.Source with { Loan = r.Source.Loan with { Repayments = [.. r.Source.Loan.Repayments, new StoredRepayment(Guid.Empty, repayment, null, null, 0)] } };
        if (r.Source.Loan.Source == ScheduleSource.Bank && r.Source.Loan.Bank.Count > 0)
        {
            var merged = BankSchedule.Merge(terms, computed!, LoanReading.BankRows(r.Source.Loan));
            if (merged.TryGetValue(out var schedule) && r.Schedule.Rows.Any(x => x.FromBank))
                return Result.Success((r, new Reading(with, today, schedule!)));
            // The table no longer fits with the repayment (recording it would be refused): the calculation on both sides.
            return Result.Success((new Reading(r.Source, today, LoanReading.Computed(r.Terms)), new Reading(with, today, computed!)));
        }
        return Result.Success((r, new Reading(with, today, computed!)));
    }
}
