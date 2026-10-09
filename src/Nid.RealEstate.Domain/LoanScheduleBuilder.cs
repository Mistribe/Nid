using Nid.RealEstate.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.RealEstate.Domain;

/// <summary>
/// loans.md §3 and spec 3A1 §4: the schedule, exact to the cent. Interest multiplies first; the contractual last payment
/// absorbs the rounding residue; insurance sits outside the loan payment; early repayments apply right after the payment
/// due on or before their date.
/// </summary>
public static class LoanScheduleBuilder
{
    public const int MaxRows = 1200;

    public static Result<LoanSchedule> Build(LoanTerms t)
    {
        if (LoanValidation.Check(t) is { } refused) return Result.Failure<LoanSchedule>(refused);

        var phases = t.Phases.Select((p, i) => (Phase: p, Index: i)).OrderBy(x => x.Phase.Seq).ToList();
        var starts = new int[phases.Count];
        var next = 1;
        for (var i = 0; i < phases.Count; i++) { starts[i] = next; next += phases[i].Phase.Payments ?? MaxRows + 1; }
        int? knownEnd = phases[^1].Phase.Payments is null ? null : next;

        var rows = new List<ScheduleRow>();
        var warnings = new List<ScheduleWarning>();
        var balance = t.Principal;
        var payment = 0m;
        var overrideActive = false;
        var termReduction = 0m; // reduce-term repayments made outside an annuity, kept out of the next annuity's payment
        var current = -1;
        var projected = false;
        var exceeded = false;

        var pending = t.Events.Select((e, i) => (Event: e, Index: i)).OrderBy(x => x.Event.OnDate).ToList();
        foreach (var (e, index) in pending.Where(x => x.Event.OnDate < t.FirstPaymentDate))
        {
            if (e.Amount > balance) return Refuse(LoanCodes.EventAboveBalance, "An early repayment exceeds the remaining balance.", $"events[{index}].amount");
            balance -= e.Amount;
            warnings.Add(new ScheduleWarning(LoanWarnings.EventOffPaymentDate, 0));
            if (e.Effect == RepaymentEffect.ReduceTerm) termReduction += e.Amount;
        }
        pending.RemoveAll(x => x.Event.OnDate < t.FirstPaymentDate);

        for (var k = 1; balance > 0m; k++)
        {
            if (k > MaxRows) return Refuse(LoanCodes.ScheduleTooLong, $"The schedule would exceed {MaxRows} payments.", "phases");
            var due = Due(t, k);
            var i = PhaseAt(k, starts, knownEnd);
            decimal rate;
            PhaseMode mode;
            int seq;
            if (i is null)
            {
                var last = phases[^1].Phase;
                (rate, mode, seq) = (last.AnnualRate, PhaseMode.Annuity, last.Seq);
                if (!projected)
                {
                    projected = true;
                    warnings.Add(new ScheduleWarning(LoanWarnings.ProjectionUsesLastRate, k));
                    payment = Annuity(balance + termReduction, rate, t.TotalPayments - (k - 1));
                    termReduction = 0m;
                    overrideActive = false;
                }
            }
            else
            {
                var (ph, _) = phases[i.Value];
                (rate, mode, seq) = (ph.AnnualRate, ph.Mode, ph.Seq);
                if (i.Value != current)
                {
                    current = i.Value;
                    if (ph.OpeningBalanceOverride is { } opening) balance = opening;
                    if (mode == PhaseMode.Annuity)
                    {
                        (payment, overrideActive) = ph.PaymentOverride is { } imposed
                            ? (imposed, true)
                            : (Annuity(balance + termReduction, rate, t.TotalPayments - (k - 1)), false);
                        termReduction = 0m;
                    }
                }
            }

            var before = balance;
            var interest = k == 1 && t.FirstPeriod == FirstPeriod.ProrataActual365
                ? RoundInterest(balance * rate * Period.DaysBetween(t.DisbursedOn, t.FirstPaymentDate) / 365m, t.Rounding)
                : RoundInterest(balance * rate / 12m, t.Rounding);
            // From the contractual last payment on, every mode settles the balance; only an imposed annuity payment stays.
            var settle = k >= t.TotalPayments && !(mode == PhaseMode.Annuity && overrideActive);
            decimal pay, principal;
            switch (mode)
            {
                case PhaseMode.DeferredTotal:
                    (pay, principal) = settle ? (balance + interest, balance) : (0m, 0m);
                    balance = settle ? 0m : balance + interest;
                    break;
                case PhaseMode.DeferredPartial:
                    (pay, principal) = settle ? (balance + interest, balance) : (interest, 0m);
                    if (settle) balance = 0m;
                    break;
                default:
                    if (overrideActive && payment <= interest)
                        return Refuse(LoanCodes.PaymentBelowInterest, "The imposed payment does not cover the interest: the loan would never end.",
                            $"phases[{phases[current].Index}].paymentOverride");
                    pay = settle ? balance + interest : payment;
                    pay = Math.Min(pay, balance + interest);
                    principal = pay - interest;
                    balance -= principal;
                    break;
            }
            if (k > t.TotalPayments && !exceeded)
            {
                exceeded = true;
                warnings.Add(new ScheduleWarning(LoanWarnings.ExceedsTotalPayments, k));
            }

            var nextDue = Due(t, k + 1);
            decimal? early = null;
            foreach (var (e, index) in pending.Where(x => x.Event.OnDate < nextDue).ToList())
            {
                if (e.Amount > balance) return Refuse(LoanCodes.EventAboveBalance, "An early repayment exceeds the remaining balance.", $"events[{index}].amount");
                if (e.OnDate != due) warnings.Add(new ScheduleWarning(LoanWarnings.EventOffPaymentDate, k));
                balance -= e.Amount;
                early = (early ?? 0m) + e.Amount;
                if (e.Effect == RepaymentEffect.ReducePayment)
                {
                    if (mode == PhaseMode.Annuity)
                    {
                        if (overrideActive)
                        {
                            warnings.Add(new ScheduleWarning(LoanWarnings.OverrideIgnoredAfterEarlyRepayment, k));
                            overrideActive = false;
                        }
                        payment = Annuity(balance, rate, t.TotalPayments - k);
                    }
                }
                else if (mode != PhaseMode.Annuity)
                {
                    termReduction += e.Amount;
                }
            }
            pending.RemoveAll(x => x.Event.OnDate < nextDue);

            rows.Add(new ScheduleRow(k, due, seq, rate, pay, interest, principal, Insurance(t, due, before), balance, early));
        }
        if (pending.Count > 0) return Refuse(LoanCodes.EventAboveBalance, "An early repayment is dated after the loan is repaid.", $"events[{pending[0].Index}].amount");
        return Result.Success(new LoanSchedule(rows, warnings));
    }

    internal static LocalDate Due(LoanTerms t, int k) => t.FirstPaymentDate.PlusMonths(k - 1);

    internal static decimal HalfUp(decimal x) => decimal.Round(x, 2, MidpointRounding.AwayFromZero);

    private static decimal RoundInterest(decimal x, InterestRounding rounding)
        => rounding == InterestRounding.Truncate ? decimal.Round(x, 2, MidpointRounding.ToZero) : HalfUp(x);

    /// <summary>C × r ÷ (1 − 1 ÷ (1 + r)^n), half up; C ÷ n at rate 0; the whole balance when no payment is left.</summary>
    internal static decimal Annuity(decimal balance, decimal annualRate, int n)
    {
        if (n <= 0) return balance;
        if (annualRate == 0m) return HalfUp(balance / n);
        var r = annualRate / 12m;
        return HalfUp(balance * r / (1m - 1m / Pow(1m + r, n)));
    }

    private static decimal Pow(decimal x, int n)
    {
        var result = 1m;
        while (n > 0)
        {
            if ((n & 1) == 1) result *= x;
            n >>= 1;
            if (n > 0) x *= x;
        }
        return result;
    }

    private static int? PhaseAt(int k, int[] starts, int? knownEnd)
    {
        if (knownEnd is { } end && k >= end) return null;
        for (var i = starts.Length - 1; i >= 0; i--)
            if (k >= starts[i]) return i;
        return 0;
    }

    private static decimal Insurance(LoanTerms t, LocalDate due, decimal before)
        => t.Insurances
            .Where(ins => ins.StartsOn <= due && (ins.EndsOn is not { } end || due < end))
            .Sum(ins => ins.Mode switch
            {
                InsuranceMode.FixedMonthly => ins.MonthlyAmount!.Value,
                InsuranceMode.RateOnInitial => HalfUp(ins.AnnualRate!.Value * t.Principal / 12m),
                _ => HalfUp(ins.AnnualRate!.Value * before / 12m),
            });

    private static Result<LoanSchedule> Refuse(string code, string message, string field)
        => Result.Failure<LoanSchedule>(LoanValidation.Refuse(field, message, code));
}
