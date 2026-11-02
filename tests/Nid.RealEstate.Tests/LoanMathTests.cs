using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Tests;

public class LoanMathTests
{
    private static (LoanTerms Terms, LoanSchedule Schedule) Golden(Func<LoanTerms, LoanTerms>? change = null)
    {
        var terms = (change ?? (t => t))(Loans.Golden());
        return (terms, Loans.Ok(LoanScheduleBuilder.Build(terms)));
    }

    [Fact]
    public void Before_disbursement_nothing_is_owed_and_between_disbursement_and_the_first_payment_the_principal_is()
    {
        var (t, s) = Golden();
        var before = LoanMath.At(t, s, Loans.Disbursed.PlusDays(-1));
        Assert.Equal((0m, 0m, 240), (before.Balance, before.InterestPaid, before.PaymentsLeft));
        var between = LoanMath.At(t, s, Loans.Disbursed.PlusDays(3));
        Assert.Equal(200_000m, between.Balance);
        Assert.Equal(Loans.First, between.NextPaymentOn);
        Assert.Equal(1159.92m, between.NextPaymentTotal);
    }

    [Fact]
    public void After_60_payments_the_figures_match_section_4()
    {
        var (t, s) = Golden(x => x with { Fees = [new LoanFee(FeeKind.File, 1_000m, Loans.Disbursed), new LoanFee(FeeKind.Other, 200m, new LocalDate(2030, 1, 1))] });
        var f = LoanMath.At(t, s, Loans.First.PlusMonths(59));
        Assert.Equal(162253.12m, f.Balance);
        Assert.Equal((37746.88m, 31848.32m), (f.PrincipalRepaid, f.InterestPaid));
        Assert.Equal(1_000m, f.FeesPaid);
        Assert.Equal(180, f.PaymentsLeft);
        Assert.Equal(new LocalDate(2044, 1, 15), f.EndDate);
        Assert.Equal((1, 0.035m), (f.CurrentPhaseSeq, f.CurrentRate));
        Assert.Equal(78380.66m + 1_200m, f.TotalCost);
        Assert.Equal(31848.32m + 1_000m, f.CostPaid);
        Assert.Equal(f.TotalCost - f.CostPaid, f.CostRemaining);
    }

    [Fact]
    public void After_the_end_nothing_is_left()
    {
        var (t, s) = Golden();
        var f = LoanMath.At(t, s, new LocalDate(2050, 1, 1));
        Assert.Equal((0m, 0, (LocalDate?)null), (f.Balance, f.PaymentsLeft, f.NextPaymentOn));
        Assert.Equal(200_000m, f.PrincipalRepaid);
    }

    [Fact]
    public void The_balance_before_an_off_date_repayment_ignores_it()
    {
        var on = Loans.First.PlusMonths(59).PlusDays(10);
        var (t, s) = Golden(x => x with { Events = [new EarlyRepayment(on, 10_000m, RepaymentEffect.ReduceTerm)] });
        Assert.Equal(162253.12m, LoanMath.At(t, s, on.PlusDays(-1)).Balance);
        Assert.Equal(152253.12m, LoanMath.At(t, s, on).Balance);
        Assert.Equal(37746.88m + 10_000m, LoanMath.At(t, s, on).PrincipalRepaid);
    }

    [Fact]
    public void The_simulation_compares_both_effects_with_section_4s_savings()
    {
        var sim = Loans.Ok(LoanMath.SimulateEarlyRepayment(Loans.Golden(), Loans.First.PlusMonths(59), 10_000m, null, 300m));
        Assert.Equal(2, sim.Outcomes.Count);
        var term = sim.Outcomes.Single(o => o.Effect == RepaymentEffect.ReduceTerm);
        Assert.Equal((240, 226, 6566.58m, 6266.58m), (term.PaymentsBefore, term.PaymentsAfter, term.InterestSaved, term.NetSaving));
        Assert.Equal((1159.92m, 1159.92m), (term.PaymentBefore, term.PaymentAfter));
        var payment = sim.Outcomes.Single(o => o.Effect == RepaymentEffect.ReducePayment);
        Assert.Equal((1159.92m, 1088.43m, 2867.80m), (payment.PaymentBefore, payment.PaymentAfter, payment.InterestSaved));
        Assert.Equal((78380.66m, 75512.86m), (payment.InterestBefore, payment.InterestAfter));
    }

    [Fact]
    public void A_simulation_for_one_effect_returns_that_effect_only()
        => Assert.Single(Loans.Ok(LoanMath.SimulateEarlyRepayment(Loans.Golden(), Loans.First.PlusMonths(59), 10_000m, RepaymentEffect.ReduceTerm, 0m)).Outcomes);

    [Fact]
    public void A_simulated_repayment_of_zero_changes_nothing()
    {
        var o = Loans.Ok(LoanMath.SimulateEarlyRepayment(Loans.Golden(), Loans.First.PlusMonths(59), 0m, RepaymentEffect.ReducePayment, 0m)).Outcomes.Single();
        Assert.Equal((o.PaymentsBefore, o.InterestBefore, o.PaymentBefore), (o.PaymentsAfter, o.InterestAfter, o.PaymentAfter));
        Assert.Equal(0m, o.InterestSaved);
    }

    [Fact]
    public void A_simulation_above_the_balance_is_refused_at_amount()
    {
        var f = Loans.Fail(LoanMath.SimulateEarlyRepayment(Loans.Golden(), Loans.First.PlusMonths(59), 500_000m, null, 0m));
        Assert.Equal((LoanCodes.EventAboveBalance, "amount"), (f.Code, Loans.FieldOf(f)));
        var negative = Loans.Fail(LoanMath.SimulateEarlyRepayment(Loans.Golden(), Loans.First, 10m, null, -1m));
        Assert.Equal("penalty", Loans.FieldOf(negative));
        var late = Loans.Fail(LoanMath.SimulateEarlyRepayment(Loans.Golden(), new LocalDate(2045, 1, 15), 10m, null, 0m));
        Assert.Equal((LoanCodes.EventAboveBalance, "amount"), (late.Code, Loans.FieldOf(late)));
    }

    [Fact]
    public void A_fee_dated_before_disbursement_counts_before_disbursement()
    {
        var (t, s) = Golden(x => x with { Fees = [new LoanFee(FeeKind.File, 1_000m, Loans.Disbursed.PlusDays(-10))] });
        var f = LoanMath.At(t, s, Loans.Disbursed.PlusDays(-1));
        Assert.Equal((0m, 1_000m, 1_000m), (f.Balance, f.FeesPaid, f.CostPaid));
        Assert.Equal(f.TotalCost - 1_000m, f.CostRemaining);
    }

    [Fact]
    public void A_simulation_dated_before_disbursement_is_refused_at_on()
    {
        var f = Loans.Fail(LoanMath.SimulateEarlyRepayment(Loans.Golden(), Loans.Disbursed.PlusDays(-1), 10m, null, 0m));
        Assert.Equal((LoanCodes.InvalidLoan, "on"), (f.Code, Loans.FieldOf(f)));
    }

    [Fact]
    public void A_fixed_insurance_shows_in_the_next_payment_and_the_insurance_paid()
    {
        var (t, s) = Golden(x => x with { Insurances = [new LoanInsurance(InsuranceMode.FixedMonthly, 20m, null, Loans.First)] });
        Assert.Equal(1179.92m, LoanMath.At(t, s, Loans.Disbursed.PlusDays(3)).NextPaymentTotal);
        Assert.Equal(1_200m, LoanMath.At(t, s, Loans.First.PlusMonths(59)).InsurancePaid);
    }

    [Fact]
    public void A_repayment_is_not_made_before_its_date()
    {
        var (t, s) = Golden(x => x with { Events = [new EarlyRepayment(Loans.Disbursed.PlusDays(5), 10_000m, RepaymentEffect.ReduceTerm)] });
        Assert.Equal(200_000m, LoanMath.At(t, s, Loans.Disbursed.PlusDays(4)).Balance);
        var after = LoanMath.At(t, s, Loans.Disbursed.PlusDays(6));
        Assert.Equal((190_000m, 10_000m), (after.Balance, after.PrincipalRepaid));
    }

    [Fact]
    public void A_fee_counts_from_its_own_day_before_and_after_disbursement()
    {
        LocalDate signing = Loans.Disbursed.PlusDays(-10), later = new(2030, 1, 1);
        var (t, s) = Golden(x => x with { Fees = [new LoanFee(FeeKind.File, 1_000m, signing), new LoanFee(FeeKind.Other, 200m, later)] });
        Assert.Equal(1_000m, LoanMath.At(t, s, signing).FeesPaid);
        Assert.Equal(1_200m, LoanMath.At(t, s, later).FeesPaid);
    }

    [Fact]
    public void Before_disbursement_the_next_payment_includes_the_insurance()
    {
        var (t, s) = Golden(x => x with { Insurances = [new LoanInsurance(InsuranceMode.FixedMonthly, 20m, null, Loans.First)] });
        Assert.Equal(1179.92m, LoanMath.At(t, s, Loans.Disbursed.PlusDays(-1)).NextPaymentTotal);
    }

    [Fact]
    public void An_empty_schedule_has_no_next_payment_before_disbursement()
    {
        var f = LoanMath.At(Loans.Golden(), new LoanSchedule([], []), Loans.Disbursed.PlusDays(-1));
        Assert.Equal(((int?)null, (decimal?)null, (LocalDate?)null, (decimal?)null), (f.CurrentPhaseSeq, f.CurrentRate, f.NextPaymentOn, f.NextPaymentTotal));
    }

    [Fact]
    public void The_current_phase_is_the_next_payment_s_then_the_last_one_s()
    {
        var (t, s) = Golden(x => x with { Phases = [new LoanPhase(1, PhaseMode.Annuity, 0.03m, RateType.Fixed, 12), new LoanPhase(2, PhaseMode.Annuity, 0.04m, RateType.Fixed)] });
        var during = LoanMath.At(t, s, Loans.First);
        Assert.Equal((1, 0.03m), (during.CurrentPhaseSeq, during.CurrentRate));
        var after = LoanMath.At(t, s, new LocalDate(2050, 1, 1));
        Assert.Equal((2, 0.04m), (after.CurrentPhaseSeq, after.CurrentRate));
    }

    [Fact]
    public void The_cost_paid_adds_the_insurance_to_the_interest()
    {
        var (t, s) = Golden(x => x with { Insurances = [new LoanInsurance(InsuranceMode.FixedMonthly, 20m, null, Loans.First)] });
        var f = LoanMath.At(t, s, Loans.First.PlusMonths(59));
        Assert.Equal(31848.32m + 1_200m, f.CostPaid);
        Assert.Equal(f.TotalCost - f.CostPaid, f.CostRemaining);
    }

    [Fact]
    public void A_simulation_after_the_last_payment_has_no_payment_left()
    {
        var o = Loans.Ok(LoanMath.SimulateEarlyRepayment(Loans.Golden(), new LocalDate(2050, 1, 1), 0m, RepaymentEffect.ReduceTerm, 0m)).Outcomes.Single();
        Assert.Equal((0m, 0m), (o.PaymentBefore, o.PaymentAfter));
    }

    [Fact]
    public void A_simulation_with_an_unknown_effect_is_refused_at_effect()
    {
        var f = Loans.Fail(LoanMath.SimulateEarlyRepayment(Loans.Golden(), Loans.First, 10m, (RepaymentEffect)9, 0m));
        Assert.Equal((LoanCodes.InvalidLoan, "effect"), (f.Code, Loans.FieldOf(f)));
    }
}
