using Nid.RealEstate.Contracts;
using UnambitiousFx.Functional.Failures;

namespace Nid.RealEstate.Domain;

/// <summary>loans.md §11's errors the engine owns. The first refusal wins; indices are positions in the lists given.</summary>
public static class LoanValidation
{
    public static Failure? Check(LoanTerms t)
    {
        if (t.Principal <= 0m) return Refuse("principal", "The principal is positive.");
        if (t.FirstPaymentDate < t.DisbursedOn) return Refuse("firstPaymentDate", "The first payment is on or after the disbursement.");
        if (t.TotalPayments is < 1 or > 600) return Refuse("totalPayments", "A loan has 1 to 600 payments.");
        if (t.Phases.Count == 0) return Refuse("phases", "A loan has at least one phase.");

        var bySeq = t.Phases.Select((p, i) => (Phase: p, Index: i)).OrderBy(x => x.Phase.Seq).ToList();
        for (var n = 0; n < bySeq.Count; n++)
            if (bySeq[n].Phase.Seq != n + 1)
                return Refuse($"phases[{bySeq[n].Index}].seq", "Phases are numbered 1, 2, 3… without gap.");
        for (var n = 0; n < bySeq.Count; n++)
        {
            var (p, i) = bySeq[n];
            if (p.Payments is null && n < bySeq.Count - 1) return Refuse($"phases[{i}].payments", "Only the last phase may run to the end.");
            if (p.Payments is <= 0) return Refuse($"phases[{i}].payments", "A phase has at least one payment.");
            if (p.AnnualRate < 0m || p.AnnualRate >= 1m) return Refuse($"phases[{i}].annualRate", "A rate is at least 0 and below 1.");
            if (p.PaymentOverride is <= 0m) return Refuse($"phases[{i}].paymentOverride", "An imposed payment is positive.");
            if (p.OpeningBalanceOverride is <= 0m) return Refuse($"phases[{i}].openingBalanceOverride", "An opening balance is positive.");
        }
        if (t.Phases.Sum(p => p.Payments ?? 0) > t.TotalPayments) return Refuse("phases", "The phases have more payments than the loan.");

        for (var i = 0; i < t.Insurances.Count; i++)
        {
            var ins = t.Insurances[i];
            if (ins.Mode == InsuranceMode.FixedMonthly && ins.MonthlyAmount is not > 0m)
                return Refuse($"insurances[{i}].monthlyAmount", "A fixed insurance has a positive monthly amount.");
            if (ins.Mode != InsuranceMode.FixedMonthly && (ins.AnnualRate is not { } rate || rate < 0m || rate >= 1m))
                return Refuse($"insurances[{i}].annualRate", "A rated insurance has a rate of at least 0 and below 1.");
            if (ins.EndsOn is { } end && end <= ins.StartsOn) return Refuse($"insurances[{i}].endsOn", "An insurance ends after it starts.");
        }
        for (var i = 0; i < t.Fees.Count; i++)
            if (t.Fees[i].Amount <= 0m) return Refuse($"fees[{i}].amount", "A fee is positive.");
        for (var i = 0; i < t.Events.Count; i++)
        {
            if (t.Events[i].Amount <= 0m) return Refuse($"events[{i}].amount", "An early repayment is positive.");
            if (t.Events[i].OnDate < t.DisbursedOn) return Refuse($"events[{i}].onDate", "An early repayment is on or after the disbursement.");
        }
        return null;
    }

    internal static Failure Refuse(string field, string message, string code = LoanCodes.InvalidLoan)
        => new(code, message, new Dictionary<string, object?> { ["field"] = field });
}
