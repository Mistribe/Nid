using Nid.Core.Contracts;
using Nid.RealEstate.Application;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional.Failures;

namespace Nid.RealEstate.Tests;

/// <summary>Spec 3A2 §4 and §5: a contract's refusals answer at the body's own paths, with the real engine.</summary>
public class LoanPathsTests
{
    private static StoredRepayment Repayment(LocalDate on, decimal amount)
        => new(Guid.CreateVersion7(), new EarlyRepayment(on, amount, RepaymentEffect.ReduceTerm), null, null, 1);

    /// <summary>What the creation and contract routes do: the contract with the stored repayments, through the engine, then mapped.</summary>
    private static Failure Refusal(LoanTerms contract, params StoredRepayment[] stored)
        => LoanPaths.ForContract(Loans.Fail(LoanScheduleBuilder.Build(
            LoanReading.Terms(Currency.Eur, Loans.Stored(terms: contract).Contract, [], stored))));

    private static (string Code, string? Field) Located(Failure failure) => (failure.Code, LoanPaths.FieldOf(failure));

    [Fact]
    public void A_contract_refusal_answers_at_the_bodys_own_path()
    {
        var twoPhases = Loans.Golden() with
        {
            Phases = [new LoanPhase(1, PhaseMode.Annuity, 0.035m, RateType.Fixed, 60), new LoanPhase(2, PhaseMode.Annuity, 1.2m, RateType.Variable)],
        };
        Assert.Equal((LoanCodes.InvalidLoan, "phases[1].annualRate"), Located(Refusal(twoPhases)));

        var insured = Loans.Golden() with { Insurances = [new LoanInsurance(InsuranceMode.FixedMonthly, 20m, null, Loans.First, Loans.First)] };
        Assert.Equal((LoanCodes.InvalidLoan, "insurances[0].endsOn"), Located(Refusal(insured)));

        var imposed = Loans.Golden() with { Phases = [new LoanPhase(1, PhaseMode.Annuity, 0.035m, RateType.Fixed, null, 100m)] };
        Assert.Equal((LoanCodes.PaymentBelowInterest, "phases[0].paymentOverride"), Located(Refusal(imposed)));

        Assert.Equal((LoanCodes.InvalidLoan, "principal"), Located(Refusal(Loans.Annuity(0m, 0.035m, 240))));
    }

    [Fact]
    public void A_contract_that_makes_a_stored_repayment_impossible_answers_at_its_rank()
    {
        var late = Repayment(new LocalDate(2030, 3, 15), 100_000m);
        var early = Repayment(new LocalDate(2025, 3, 15), 5_000m);
        Assert.True(LoanScheduleBuilder.Build(LoanReading.Terms(Currency.Eur, Loans.Stored().Contract, [], [late, early])).TryGetValue(out _));

        // 90 000 leaves about 63 000 in March 2030: the second repayment by date, 100 000, no longer fits.
        Assert.Equal((LoanCodes.EventAboveBalance, "repayments[1].amount"), Located(Refusal(Loans.Annuity(90_000m, 0.035m, 240), late, early)));
    }

    [Fact]
    public void Same_day_repayments_answer_at_their_rank_in_the_detail() // Review Focus #1
    {
        var day = new LocalDate(2030, 3, 15);
        var a = Repayment(day, 60_000m);
        var b = Repayment(day, 60_001m);
        var (first, second) = a.Id.CompareTo(b.Id) < 0 ? (a, b) : (b, a);
        var smaller = Loans.Annuity(90_000m, 0.035m, 240);

        // Sent in the opposite order on purpose: the rank is date then id, never the order of arrival.
        Assert.Equal((LoanCodes.EventAboveBalance, "repayments[1].amount"), Located(Refusal(smaller, second, first)));
        Assert.Equal(second.Repayment.Amount,
            LoanReading.Terms(Currency.Eur, Loans.Stored(terms: smaller).Contract, [], [second, first]).Events[1].Amount);
        var detail = LoanViews.Detail(Loans.Item(), Loans.Stored(repayments: [second, first]), Loans.First, canWrite: true);
        Assert.Equal([first.Id, second.Id], detail.Repayments.Select(r => r.Id));
    }
}
