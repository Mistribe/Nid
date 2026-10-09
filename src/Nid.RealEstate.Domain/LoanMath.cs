using Nid.RealEstate.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.RealEstate.Domain;

/// <summary>loans.md §7 and §8: a loan at a date, and the early-repayment simulation (read only: nothing is written).</summary>
public static class LoanMath
{
    /// <summary>
    /// Balance after the last payment due on or before <paramref name="d"/> (an early repayment applied after it counts only
    /// from its own date); the principal between disbursement and the first payment; 0 before disbursement.
    /// <c>InterestPaid</c> is the interest accrued to <paramref name="d"/>, capitalised interest included.
    /// </summary>
    public static LoanFigures At(LoanTerms t, LoanSchedule s, LocalDate d)
    {
        var rows = s.Rows;
        var totalCost = rows.Sum(r => r.Interest) + rows.Sum(r => r.Insurance) + t.Fees.Sum(f => f.Amount);
        if (d < t.DisbursedOn)
            return new LoanFigures(0m, 0m, 0m, 0m, 0m, rows.FirstOrDefault()?.PhaseSeq, rows.FirstOrDefault()?.AnnualRate, rows.Count,
                rows.FirstOrDefault()?.DueDate, rows.FirstOrDefault() is { } r0 ? r0.Payment + r0.Insurance : null,
                rows.LastOrDefault()?.DueDate, totalCost, 0m, totalCost);

        var paid = rows.Where(r => r.DueDate <= d).ToList();
        var left = rows.Where(r => r.DueDate > d).ToList();
        var repaid = t.Events.Where(e => e.OnDate <= d).Sum(e => e.Amount);
        decimal balance;
        if (paid.Count == 0)
            balance = t.Principal - repaid;
        else
        {
            var last = paid[^1];
            var nextDue = LoanScheduleBuilder.Due(t, last.Number + 1);
            // Repayments applied after this row but dated after d are not yet made.
            var notYet = t.Events.Where(e => e.OnDate >= last.DueDate && e.OnDate < nextDue && e.OnDate > d).Sum(e => e.Amount);
            balance = last.Balance + notYet;
        }
        var interest = paid.Sum(r => r.Interest);
        var insurance = paid.Sum(r => r.Insurance);
        var fees = t.Fees.Where(f => f.OnDate <= d).Sum(f => f.Amount);
        var next = left.FirstOrDefault();
        var reference = next ?? rows.LastOrDefault();
        return new LoanFigures(
            balance, paid.Sum(r => r.Principal) + repaid, interest, insurance, fees,
            reference?.PhaseSeq, reference?.AnnualRate, left.Count,
            next?.DueDate, next is null ? null : next.Payment + next.Insurance,
            rows.LastOrDefault()?.DueDate, totalCost, interest + insurance + fees, totalCost - (interest + insurance + fees));
    }

    public static Result<EarlyRepaymentSimulation> SimulateEarlyRepayment(
        LoanTerms t, LocalDate on, decimal amount, RepaymentEffect? effect, decimal penalty)
    {
        if (amount < 0m) return Result.Failure<EarlyRepaymentSimulation>(LoanValidation.Refuse("amount", "An early repayment is not negative."));
        if (penalty < 0m) return Result.Failure<EarlyRepaymentSimulation>(LoanValidation.Refuse("penalty", "A penalty is not negative."));
        var before = LoanScheduleBuilder.Build(t);
        if (!before.TryGetValue(out var b)) return Result.Failure<EarlyRepaymentSimulation>(before.TryGetFailure(out var f) ? f! : throw new InvalidOperationException());

        var outcomes = new List<RepaymentOutcome>();
        foreach (var e in effect is { } one ? new[] { one } : new[] { RepaymentEffect.ReduceTerm, RepaymentEffect.ReducePayment })
        {
            LoanSchedule a;
            if (amount == 0m)
                a = b!;
            else
            {
                var after = LoanScheduleBuilder.Build(t with { Events = [.. t.Events, new EarlyRepayment(on, amount, e)] });
                if (after.TryGetFailure(out var failure))
                    return Result.Failure<EarlyRepaymentSimulation>(failure!.Code == LoanCodes.EventAboveBalance
                        ? LoanValidation.Refuse("amount", failure.Message, LoanCodes.EventAboveBalance)
                        : failure);
                after.TryGetValue(out var schedule);
                a = schedule!;
            }
            decimal Interest(LoanSchedule s) => s.Rows.Sum(r => r.Interest);
            decimal PaymentAfterOn(LoanSchedule s) => s.Rows.FirstOrDefault(r => r.DueDate > on)?.Payment ?? 0m;
            var saved = Interest(b!) - Interest(a);
            outcomes.Add(new RepaymentOutcome(e, PaymentAfterOn(b!), PaymentAfterOn(a), b!.Rows.LastOrDefault()?.DueDate, a.Rows.LastOrDefault()?.DueDate,
                b.Rows.Count, a.Rows.Count, Interest(b), Interest(a), saved, saved - penalty));
        }
        return Result.Success(new EarlyRepaymentSimulation(on, amount, penalty, outcomes));
    }
}
