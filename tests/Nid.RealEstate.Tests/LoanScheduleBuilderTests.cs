using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Tests;

public class LoanScheduleBuilderTests
{
    private static LoanSchedule Build(LoanTerms t) => Loans.Ok(LoanScheduleBuilder.Build(t));
    private static decimal TotalInterest(LoanSchedule s) => s.Rows.Sum(r => r.Interest);

    [Fact]
    public void The_golden_case_matches_loans_md_section_4()
    {
        var s = Build(Loans.Golden());
        Assert.Equal(240, s.Rows.Count);
        Assert.Equal(1159.92m, s.Rows[0].Payment);
        Assert.Equal((583.33m, 576.59m, 199423.41m), (s.Rows[0].Interest, s.Rows[0].Principal, s.Rows[0].Balance));
        Assert.Equal((475.24m, 684.68m, 162253.12m), (s.Rows[59].Interest, s.Rows[59].Principal, s.Rows[59].Balance));
        Assert.Equal((344.50m, 815.42m, 117298.78m), (s.Rows[119].Interest, s.Rows[119].Principal, s.Rows[119].Balance));
        Assert.Equal((1159.78m, 3.37m, 1156.41m, 0m), (s.Rows[239].Payment, s.Rows[239].Interest, s.Rows[239].Principal, s.Rows[239].Balance));
        Assert.Equal(78380.66m, TotalInterest(s));
        var first60 = s.Rows.Take(60).ToList();
        Assert.Equal((69595.20m, 37746.88m, 31848.32m), (first60.Sum(r => r.Payment), first60.Sum(r => r.Principal), first60.Sum(r => r.Interest)));
        Assert.Equal(new LocalDate(2044, 1, 15), s.Rows[239].DueDate);
        Assert.Empty(s.Warnings);
    }

    [Fact]
    public void An_early_repayment_reducing_the_term_matches_section_4()
    {
        var golden = Loans.Golden();
        var s = Build(golden with { Events = [new EarlyRepayment(Loans.First.PlusMonths(59), 10_000m, RepaymentEffect.ReduceTerm)] });
        Assert.Equal(226, s.Rows.Count);
        Assert.Equal(832.08m, s.Rows[^1].Payment);
        Assert.Equal(71814.08m, TotalInterest(s));
        Assert.Equal(6566.58m, TotalInterest(Build(golden)) - TotalInterest(s));
        Assert.Equal(10_000m, s.Rows[59].EarlyRepayment);
        Assert.Equal(1159.92m, s.Rows[60].Payment);
    }

    [Fact]
    public void An_early_repayment_reducing_the_payment_matches_section_4()
    {
        var golden = Loans.Golden();
        var s = Build(golden with { Events = [new EarlyRepayment(Loans.First.PlusMonths(59), 10_000m, RepaymentEffect.ReducePayment)] });
        Assert.Equal(240, s.Rows.Count);
        Assert.Equal(1088.43m, s.Rows[60].Payment);
        Assert.Equal(1088.69m, s.Rows[^1].Payment);
        Assert.Equal(75512.86m, TotalInterest(s));
        Assert.Equal(2867.80m, TotalInterest(Build(golden)) - TotalInterest(s));
    }

    [Fact]
    public void A_zero_rate_loan_with_a_total_deferral_matches_section_4()
    {
        var s = Build(Loans.Golden() with
        {
            Principal = 40_000m,
            Phases = [new LoanPhase(1, PhaseMode.DeferredTotal, 0m, RateType.Fixed, 60), new LoanPhase(2, PhaseMode.Annuity, 0m, RateType.Fixed)],
        });
        Assert.Equal(240, s.Rows.Count);
        Assert.All(s.Rows.Take(60), r => Assert.Equal((0m, 40_000m), (r.Payment, r.Balance)));
        Assert.Equal(222.22m, s.Rows[60].Payment);
        Assert.Equal(222.62m, s.Rows[^1].Payment);
    }

    [Fact]
    public void A_total_deferral_capitalises_interest_and_a_partial_one_pays_it()
    {
        var total = Build(Loans.Annuity(12_000m, 0.06m, 24) with { Phases = [new LoanPhase(1, PhaseMode.DeferredTotal, 0.06m, RateType.Fixed, 2), new LoanPhase(2, PhaseMode.Annuity, 0.06m, RateType.Fixed)] });
        Assert.Equal((0m, 60.00m, 12_060.00m), (total.Rows[0].Payment, total.Rows[0].Interest, total.Rows[0].Balance));
        Assert.Equal((0m, 60.30m, 12_120.30m), (total.Rows[1].Payment, total.Rows[1].Interest, total.Rows[1].Balance));
        var partial = Build(Loans.Annuity(12_000m, 0.06m, 24) with { Phases = [new LoanPhase(1, PhaseMode.DeferredPartial, 0.06m, RateType.Fixed, 2), new LoanPhase(2, PhaseMode.Annuity, 0.06m, RateType.Fixed)] });
        Assert.All(partial.Rows.Take(2), r => Assert.Equal((60.00m, 60.00m, 0m, 12_000m), (r.Payment, r.Interest, r.Principal, r.Balance)));
        Assert.Equal(24, partial.Rows.Count);
        Assert.Equal(0m, partial.Rows[^1].Balance);
    }

    [Fact]
    public void Truncate_rounds_only_the_interest_down()
    {
        // 1 000 × 8 % ÷ 12 = 6.666…: half up 6.67, truncated 6.66; the payment is half up in both.
        var up = Build(Loans.Annuity(1_000m, 0.08m, 12));
        var down = Build(Loans.Annuity(1_000m, 0.08m, 12, InterestRounding.Truncate));
        Assert.Equal((6.67m, 6.66m), (up.Rows[0].Interest, down.Rows[0].Interest));
        Assert.Equal(up.Rows[0].Payment, down.Rows[0].Payment);
    }

    [Fact]
    public void Interest_multiplies_first_on_a_half_cent()
    {
        // 75 × 7.6 % ÷ 12 = 0.475 exactly: multiplying first gives 0.48; precomputing 0.076 ÷ 12 would give 0.47.
        var s = Build(Loans.Annuity(75m, 0.076m, 12));
        Assert.Equal(0.48m, s.Rows[0].Interest);
    }

    [Fact]
    public void The_first_period_can_be_prorated_on_actual_days()
    {
        // 31 days from 15 January to 15 February: 200 000 × 3.5 % × 31 ÷ 365 = 594.52…
        var s = Build(Loans.Golden() with { FirstPeriod = FirstPeriod.ProrataActual365 });
        Assert.Equal(594.52m, s.Rows[0].Interest);
        // The second row is a full month again.
        Assert.Equal(Math.Round(s.Rows[0].Balance * 0.035m / 12m, 2, MidpointRounding.AwayFromZero), s.Rows[1].Interest);
    }

    [Fact]
    public void An_early_repayment_between_two_payments_applies_after_the_earlier_one()
    {
        var s = Build(Loans.Golden() with { Events = [new EarlyRepayment(Loans.First.PlusMonths(59).PlusDays(10), 10_000m, RepaymentEffect.ReduceTerm)] });
        Assert.Equal(10_000m, s.Rows[59].EarlyRepayment);
        Assert.Contains(s.Warnings, w => w == new ScheduleWarning(LoanWarnings.EventOffPaymentDate, 60));
        Assert.Equal(226, s.Rows.Count);
    }

    [Fact]
    public void An_early_repayment_before_the_first_payment_applies_on_the_disbursed_principal()
    {
        var on = Loans.Disbursed.PlusDays(5);
        var payment = Build(Loans.Annuity(12_000m, 0m, 12) with { Events = [new EarlyRepayment(on, 1_200m, RepaymentEffect.ReducePayment)] });
        Assert.Equal(900m, payment.Rows[0].Payment);
        Assert.Equal(12, payment.Rows.Count);
        var term = Build(Loans.Annuity(12_000m, 0m, 12) with { Events = [new EarlyRepayment(on, 1_200m, RepaymentEffect.ReduceTerm)] });
        Assert.Equal(1_000m, term.Rows[0].Payment);
        Assert.Equal(11, term.Rows.Count);
        Assert.Equal(800m, term.Rows[^1].Payment);
        Assert.Contains(term.Warnings, w => w == new ScheduleWarning(LoanWarnings.EventOffPaymentDate, 0));
    }

    [Fact]
    public void Phases_running_out_continue_at_the_last_rate_with_a_warning()
    {
        var s = Build(Loans.Annuity(30_000m, 0.03m, 36) with
        {
            Phases = [new LoanPhase(1, PhaseMode.Annuity, 0.02m, RateType.Fixed, 12), new LoanPhase(2, PhaseMode.Annuity, 0.04m, RateType.Variable, 12)],
        });
        Assert.Equal(36, s.Rows.Count);
        Assert.Equal(0.04m, s.Rows[24].AnnualRate);
        Assert.Contains(s.Warnings, w => w == new ScheduleWarning(LoanWarnings.ProjectionUsesLastRate, 25));
        Assert.Equal(0m, s.Rows[^1].Balance);
    }

    [Fact]
    public void An_annuity_phase_starting_mid_loan_recomputes_its_payment_and_ends_on_time()
    {
        var s = Build(Loans.Annuity(30_000m, 0.03m, 36) with
        {
            Phases = [new LoanPhase(1, PhaseMode.Annuity, 0.02m, RateType.Fixed, 12), new LoanPhase(2, PhaseMode.Annuity, 0.05m, RateType.Variable)],
        });
        Assert.NotEqual(s.Rows[11].Payment, s.Rows[12].Payment);
        Assert.Equal(36, s.Rows.Count);
        Assert.Equal(0m, s.Rows[^1].Balance);
        Assert.Empty(s.Warnings);
    }

    [Fact]
    public void An_imposed_payment_below_the_interest_is_refused()
    {
        var f = Loans.Fail(LoanScheduleBuilder.Build(Loans.Annuity(100_000m, 0.05m, 120) with
        {
            Phases = [new LoanPhase(1, PhaseMode.Annuity, 0.05m, RateType.Fixed, PaymentOverride: 100m)],
        }));
        Assert.Equal((LoanCodes.PaymentBelowInterest, "phases[0].paymentOverride"), (f.Code, Loans.FieldOf(f)));
    }

    [Fact]
    public void A_low_imposed_payment_extends_the_loan_with_a_warning_and_a_ceiling()
    {
        var s = Build(Loans.Annuity(100_000m, 0m, 12) with { Phases = [new LoanPhase(1, PhaseMode.Annuity, 0m, RateType.Fixed, PaymentOverride: 5_000m)] });
        Assert.Equal(20, s.Rows.Count);
        Assert.Contains(s.Warnings, w => w == new ScheduleWarning(LoanWarnings.ExceedsTotalPayments, 13));
        var f = Loans.Fail(LoanScheduleBuilder.Build(Loans.Annuity(1_000_000m, 0m, 12) with { Phases = [new LoanPhase(1, PhaseMode.Annuity, 0m, RateType.Fixed, PaymentOverride: 1m)] }));
        Assert.Equal(LoanCodes.ScheduleTooLong, f.Code);
    }

    [Fact]
    public void Reducing_the_payment_drops_an_imposed_payment_with_a_warning()
    {
        var s = Build(Loans.Annuity(12_000m, 0m, 12) with
        {
            Phases = [new LoanPhase(1, PhaseMode.Annuity, 0m, RateType.Fixed, PaymentOverride: 1_500m)],
            Events = [new EarlyRepayment(Loans.First, 1_500m, RepaymentEffect.ReducePayment)],
        });
        Assert.Contains(s.Warnings, w => w == new ScheduleWarning(LoanWarnings.OverrideIgnoredAfterEarlyRepayment, 1));
        Assert.Equal(818.18m, s.Rows[1].Payment); // 9 000 ÷ 11, half up
    }

    [Fact]
    public void An_early_repayment_above_the_balance_is_refused()
    {
        var f = Loans.Fail(LoanScheduleBuilder.Build(Loans.Annuity(1_000m, 0m, 10) with
        {
            Events = [new EarlyRepayment(Loans.First, 950m, RepaymentEffect.ReduceTerm)],
        }));
        Assert.Equal((LoanCodes.EventAboveBalance, "events[0].amount"), (f.Code, Loans.FieldOf(f)));
    }

    [Fact]
    public void Insurance_follows_its_mode_and_its_window_outside_the_payment()
    {
        var golden = Loans.Golden();
        var fixedOne = Build(golden with { Insurances = [new LoanInsurance(InsuranceMode.FixedMonthly, 20m, null, Loans.First.PlusMonths(2))] });
        Assert.Equal((0m, 0m, 20m), (fixedOne.Rows[0].Insurance, fixedOne.Rows[1].Insurance, fixedOne.Rows[2].Insurance));
        Assert.Equal(1159.92m, fixedOne.Rows[2].Payment);
        var initial = Build(golden with { Insurances = [new LoanInsurance(InsuranceMode.RateOnInitial, null, 0.003m, Loans.First)] });
        Assert.All(initial.Rows, r => Assert.Equal(50.00m, r.Insurance));
        var remaining = Build(golden with { Insurances = [new LoanInsurance(InsuranceMode.RateOnRemaining, null, 0.003m, Loans.First, Loans.First.PlusMonths(12))] });
        Assert.Equal((50.00m, 49.86m), (remaining.Rows[0].Insurance, remaining.Rows[1].Insurance));
        Assert.Equal(0m, remaining.Rows[12].Insurance);
        var deferred = Build(golden with
        {
            Phases = [new LoanPhase(1, PhaseMode.DeferredTotal, 0.035m, RateType.Fixed, 3), new LoanPhase(2, PhaseMode.Annuity, 0.035m, RateType.Fixed)],
            Insurances = [new LoanInsurance(InsuranceMode.FixedMonthly, 20m, null, Loans.First)],
        });
        Assert.Equal((0m, 20m), (deferred.Rows[0].Payment, deferred.Rows[0].Insurance));
    }

    [Fact]
    public void Due_dates_clamp_to_the_end_of_short_months()
    {
        var s = Build(Loans.Annuity(1_200m, 0m, 3) with { DisbursedOn = new LocalDate(2024, 1, 1), FirstPaymentDate = new LocalDate(2024, 1, 31) });
        Assert.Equal(new[] { new LocalDate(2024, 1, 31), new LocalDate(2024, 2, 29), new LocalDate(2024, 3, 31) }, s.Rows.Select(r => r.DueDate));
    }

    [Fact]
    public void A_deferral_running_to_the_end_settles_the_balance_on_the_last_payment()
    {
        foreach (int? n in new int?[] { null, 12 })
        {
            var s = Build(Loans.Annuity(100_000m, 0.03m, 12) with { Phases = [new LoanPhase(1, PhaseMode.DeferredPartial, 0.03m, RateType.Fixed, n)] });
            Assert.Equal(12, s.Rows.Count);
            Assert.All(s.Rows.Take(11), r => Assert.Equal(250.00m, r.Payment));
            Assert.Equal((100_250.00m, 0m), (s.Rows[^1].Payment, s.Rows[^1].Balance));
            Assert.Empty(s.Warnings);
        }
        var total = Build(Loans.Annuity(10_000m, 0.06m, 12) with { Phases = [new LoanPhase(1, PhaseMode.DeferredTotal, 0.06m, RateType.Fixed)] });
        Assert.Equal(12, total.Rows.Count);
        Assert.All(total.Rows.Take(11), r => Assert.Equal(0m, r.Payment));
        Assert.Equal(0m, total.Rows[^1].Balance);
        Assert.Equal(total.Rows[10].Balance + total.Rows[^1].Interest, total.Rows[^1].Payment);
    }

    [Fact]
    public void An_early_repayment_after_the_loan_ends_is_refused()
    {
        var golden = Loans.Golden();
        var late = Loans.Fail(LoanScheduleBuilder.Build(golden with { Events = [new EarlyRepayment(new LocalDate(2044, 3, 15), 100m, RepaymentEffect.ReduceTerm)] }));
        Assert.Equal((LoanCodes.EventAboveBalance, "events[0].amount"), (late.Code, Loans.FieldOf(late)));
        var obsolete = Loans.Fail(LoanScheduleBuilder.Build(golden with { Events = [
            new EarlyRepayment(Loans.First.PlusMonths(59), 10_000m, RepaymentEffect.ReduceTerm),
            new EarlyRepayment(new LocalDate(2043, 6, 15), 100m, RepaymentEffect.ReduceTerm)] }));
        Assert.Equal((LoanCodes.EventAboveBalance, "events[1].amount"), (obsolete.Code, Loans.FieldOf(obsolete)));
    }

    [Fact]
    public void An_early_repayment_during_a_deferral_follows_its_effect()
    {
        LoanSchedule Run(RepaymentEffect e) => Build(Loans.Annuity(12_000m, 0m, 24) with
        {
            Phases = [new LoanPhase(1, PhaseMode.DeferredPartial, 0m, RateType.Fixed, 6), new LoanPhase(2, PhaseMode.Annuity, 0m, RateType.Fixed)],
            Events = [new EarlyRepayment(Loans.First.PlusMonths(2), 1_200m, e)],
        });
        var term = Run(RepaymentEffect.ReduceTerm);
        Assert.Equal(666.67m, term.Rows[6].Payment);
        Assert.True(term.Rows.Count < 24);
        Assert.Equal(0m, term.Rows[^1].Balance);
        var payment = Run(RepaymentEffect.ReducePayment);
        Assert.Equal(600.00m, payment.Rows[6].Payment);
        Assert.Equal(24, payment.Rows.Count);
    }

    [Fact]
    public void Validation_refusals_come_through_the_builder()
        => Assert.Equal(LoanCodes.InvalidLoan, Loans.Fail(LoanScheduleBuilder.Build(Loans.Golden() with { Principal = -1m })).Code);
}
