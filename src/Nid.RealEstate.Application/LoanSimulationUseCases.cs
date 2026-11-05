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

/// <summary>Issue #403: stores nothing and needs read access only; the fees are typed, the penalty never computed.</summary>
public sealed record SimulateLoanRenegotiationQuery(
    ItemId ItemId, LocalDate On, decimal AnnualRate, int? Payments, decimal BankFees, decimal GuaranteeFees, decimal Penalty)
    : IQuery<LoanRenegotiationView>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

public sealed record RenegotiationSideView(
    string Payment, LocalDate? EndDate, int PaymentsLeft, string InterestLeft, string InsuranceLeft, string Fees, string TotalCost);

/// <summary>
/// Issue #403: the loan as it runs and renegotiated, from <c>On</c>; <c>BreakEvenOn</c> null is « jamais rentable ».
/// <c>Contract</c> is the contract change « Enregistrer » prepares, with the version it was read at: nothing is sent.
/// </summary>
public sealed record LoanRenegotiationView(
    LocalDate On, string AnnualRate, int Payments, string Fees, RenegotiationSideView Current, RenegotiationSideView Renegotiated,
    string Saving, LocalDate? BreakEvenOn, LoanContractView Contract);

/// <summary>Issue #404: a quote for the insurance line <c>Insurance</c> (its index in the contract), from the switch date.</summary>
public sealed record InsuranceQuote(int Insurance, InsuranceMode Mode, decimal? MonthlyAmount, decimal? AnnualRate);

public sealed record CompareLoanInsuranceQuery(ItemId ItemId, LocalDate On, IReadOnlyList<InsuranceQuote> Quotes)
    : IQuery<LoanInsuranceComparisonView>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

/// <summary>An insured person's line: what is left to pay from the date as it is and, when quoted, with the quote.</summary>
public sealed record InsuranceLineView(
    int Insurance, LoanInsuranceView Current, string CurrentLeft, LoanInsuranceView? Quote, string? QuoteLeft, string? Saving);

/// <summary>Issue #404: the lines still due from <c>On</c>, their totals, and the contract change « Enregistrer » prepares.</summary>
public sealed record LoanInsuranceComparisonView(
    LocalDate On, IReadOnlyList<InsuranceLineView> Lines, string CurrentLeft, string QuotedLeft, string Saving, LoanContractView Contract);

[RequestHandler<SimulateLoanRenegotiationQuery, LoanRenegotiationView>]
public sealed class SimulateLoanRenegotiationHandler(ILoanStore loans, IItemRegistry items)
    : IRequestHandler<SimulateLoanRenegotiationQuery, LoanRenegotiationView>
{
    public async ValueTask<Result<LoanRenegotiationView>> HandleAsync(SimulateLoanRenegotiationQuery query, CancellationToken ct = default)
    {
        if ((LoanRules.Rate(query.AnnualRate, "annualRate") ?? Fee(query.BankFees, "bankFees") ?? Fee(query.GuaranteeFees, "guaranteeFees")
             ?? Fee(query.Penalty, "penalty")) is { } refused)
            return Result.Failure<LoanRenegotiationView>(refused);

        var loaded = await LoanCheck.LoadAsync(loans, items, query.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<LoanRenegotiationView>(failure!);
        loaded.TryGetValue(out var found);

        var loan = found.Loan;
        var terms = Terms(LoanViews.CurrencyOf(found.Item), loan.Contract, loan.Fees, loan.Repayments);
        var fees = query.BankFees + query.GuaranteeFees + query.Penalty;
        var simulated = LoanSimulations.Renegotiate(terms, query.On, query.AnnualRate, query.Payments, fees);
        if (simulated.TryGetFailure(out failure))
            return Result.Failure<LoanRenegotiationView>(failure!); // the engine answers at on, annualRate or payments
        simulated.TryGetValue(out var s);

        // The phases run keep their notes (by seq); the new one has none.
        var notes = loan.Contract.Phases.ToDictionary(p => p.Phase.Seq, p => p.Note);
        var prepared = loan.Contract with
        {
            TotalPayments = s!.Terms.TotalPayments,
            Phases = s.Terms.Phases.Select(p => new LoanPhaseEntry(p, p.Seq < s.Terms.Phases.Count ? notes.GetValueOrDefault(p.Seq) : null)).ToList(),
        };
        return Result.Success(new LoanRenegotiationView(s.On, Rate(s.AnnualRate), s.Payments, Amount(fees), Side(s.Current), Side(s.Renegotiated),
            Amount(s.Saving), s.BreakEvenOn, LoanViews.ContractOf(loan, prepared)));
    }

    private static Failure? Fee(decimal amount, string field)
        => LoanRules.Amount(amount, field)
           ?? (amount < 0m ? new Failure(LoanCodes.InvalidLoan, "Fees are not negative.", new Dictionary<string, object?> { ["field"] = field }) : null);

    private static RenegotiationSideView Side(RenegotiationSide s)
        => new(Amount(s.Payment), s.EndDate, s.PaymentsLeft, Amount(s.InterestLeft), Amount(s.InsuranceLeft), Amount(s.Fees), Amount(s.TotalCost));
}

[RequestHandler<CompareLoanInsuranceQuery, LoanInsuranceComparisonView>]
public sealed class CompareLoanInsuranceHandler(ILoanStore loans, IItemRegistry items)
    : IRequestHandler<CompareLoanInsuranceQuery, LoanInsuranceComparisonView>
{
    public async ValueTask<Result<LoanInsuranceComparisonView>> HandleAsync(CompareLoanInsuranceQuery query, CancellationToken ct = default)
    {
        for (var j = 0; j < query.Quotes.Count; j++)
            if ((LoanRules.Amount(query.Quotes[j].MonthlyAmount, $"quotes[{j}].monthlyAmount")
                 ?? LoanRules.Rate(query.Quotes[j].AnnualRate, $"quotes[{j}].annualRate")) is { } refused)
                return Result.Failure<LoanInsuranceComparisonView>(refused);

        var loaded = await LoanCheck.LoadAsync(loans, items, query.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<LoanInsuranceComparisonView>(failure!);
        loaded.TryGetValue(out var found);

        var loan = found.Loan;
        var terms = Terms(LoanViews.CurrencyOf(found.Item), loan.Contract, loan.Fees, loan.Repayments);
        // The quote's dates are the comparison's: it takes the line over from the first payment after the date, until the line's end.
        var quotes = query.Quotes.Select(q => (q.Insurance, new LoanInsurance(q.Mode, q.MonthlyAmount, q.AnnualRate, query.On))).ToList();
        var compared = LoanSimulations.CompareInsurance(terms, query.On, quotes);
        if (compared.TryGetFailure(out failure))
            return Result.Failure<LoanInsuranceComparisonView>(failure!);
        compared.TryGetValue(out var c);

        var entries = loan.Contract.Insurances;
        var prepared = loan.Contract with
        {
            Insurances = c!.Insurances.Select(p => new LoanInsuranceEntry(p.Insurance, p.Line is { } i ? entries[i].Note : null)).ToList(),
        };
        return Result.Success(new LoanInsuranceComparisonView(c.On,
            c.Lines.Select(l => new InsuranceLineView(l.Index, View(l.Current, entries[l.Index].Note), Amount(l.CurrentLeft),
                l.Quote is { } q ? View(q, null) : null, Amount(l.QuoteLeft), Amount(l.CurrentLeft - l.QuoteLeft))).ToList(),
            Amount(c.CurrentLeft), Amount(c.QuotedLeft), Amount(c.CurrentLeft - c.QuotedLeft), LoanViews.ContractOf(loan, prepared)));
    }

    private static LoanInsuranceView View(LoanInsurance i, string? note)
        => new(LoanWire.Of(i.Mode), Amount(i.MonthlyAmount), Rate(i.AnnualRate), i.StartsOn, i.EndsOn, note);
}
