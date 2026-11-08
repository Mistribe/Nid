using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.RealEstate.Application;

/// <summary>Scenario spec §3: the hypothetical loans, read by the same engine as the real ones.</summary>
public sealed class ScenarioLoans : ILoanSimulator
{
    public Result<IReadOnlyList<SimulatedLoan>> Simulate(Currency currency, LocalDate purchaseOn, IReadOnlyList<ScenarioLoan> loans)
    {
        var simulated = new List<SimulatedLoan>(loans.Count);
        for (var i = 0; i < loans.Count; i++)
        {
            var loan = loans[i];
            var built = LoanScheduleBuilder.Build(Terms(currency, purchaseOn, loan));
            if (built.TryGetFailure(out var failure))
                return Result.Failure<IReadOnlyList<SimulatedLoan>>(
                    new Failure("scenario.invalid_field", failure!.Message, new Dictionary<string, object?> { ["field"] = $"loans[{i}]" }));
            built.TryGetValue(out var schedule);
            var rows = schedule!.Rows.Select(r => new SimulatedPayment(r.Number, r.DueDate, r.Payment, r.Interest, r.Principal, r.Insurance, r.Balance)).ToList();
            simulated.Add(new SimulatedLoan(loan.Name, rows, loan.FileFees + loan.GuaranteeFees));
        }
        return Result.Success<IReadOnlyList<SimulatedLoan>>(simulated);
    }

    private static LoanTerms Terms(Currency currency, LocalDate on, ScenarioLoan l)
    {
        var rate = l.Rate;
        LoanPhase[] phases = l.Deferral is { } d
            ? [new(1, d.Kind == DeferralKind.Total ? PhaseMode.DeferredTotal : PhaseMode.DeferredPartial, rate, RateType.Fixed, d.Months),
               new(2, PhaseMode.Annuity, rate, RateType.Fixed)]
            : [new(1, PhaseMode.Annuity, rate, RateType.Fixed)];
        // The engine refuses a fixed insurance of 0: no insurance is no line.
        var insurances = new List<LoanInsurance>();
        if (l.Insurance.Value != 0m) insurances.Add(Insurance(on, l.Insurance));
        var fees = new List<LoanFee>();
        if (l.FileFees != 0m) fees.Add(new(FeeKind.File, l.FileFees, on));
        if (l.GuaranteeFees != 0m) fees.Add(new(FeeKind.Guarantee, l.GuaranteeFees, on));
        return new LoanTerms(
            currency, l.Amount, on, on.PlusMonths(1), l.Months, InterestRounding.HalfUp, FirstPeriod.Full, phases, insurances, fees, []);
    }

    private static LoanInsurance Insurance(LocalDate on, ScenarioInsurance i)
        => i.Kind == ScenarioInsuranceKind.RateOnInitial
            ? new LoanInsurance(InsuranceMode.RateOnInitial, null, i.Value, on)
            : new LoanInsurance(InsuranceMode.FixedMonthly, i.Value, null, on);
}
