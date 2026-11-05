using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Tests;

/// <summary>Issues #403 and #404: the renegotiation and the insurance quote, read by the real engine on loans.md §4's loan.</summary>
public class LoanSimulationsTests
{
    /// <summary>The 60th payment of the golden case: 162 253,12 left, 180 payments to go, 46 532,34 of interest.</summary>
    private static readonly LocalDate Sixtieth = Loans.First.PlusMonths(59);

    private static LoanSchedule Build(LoanTerms t) => Loans.Ok(LoanScheduleBuilder.Build(t));

    [Fact]
    public void A_lower_rate_over_the_same_payments_compares_both_loans_from_the_date()
    {
        var sim = Loans.Ok(LoanSimulations.Renegotiate(Loans.Golden(), Sixtieth, 0.02m, null, 2_000m));

        Assert.Equal((1159.92m, new LocalDate(2044, 1, 15), 180, 78380.66m - 31848.32m, 0m),
            (sim.Current.Payment, sim.Current.EndDate, sim.Current.PaymentsLeft, sim.Current.InterestLeft, sim.Current.Fees));
        Assert.Equal((new LocalDate(2044, 1, 15), 180, 2_000m), (sim.Renegotiated.EndDate, sim.Renegotiated.PaymentsLeft, sim.Renegotiated.Fees));
        // The engine's annuity of the balance at 2 % over 180 payments, its first interest 162 253,12 × 2 % ÷ 12.
        var renegotiated = Build(sim.Terms);
        Assert.Equal(renegotiated.Rows[60].Payment, sim.Renegotiated.Payment);
        Assert.Equal(270.42m, renegotiated.Rows[60].Interest);
        Assert.True(sim.Renegotiated.Payment < 1159.92m);
        Assert.Equal(sim.Renegotiated.InterestLeft + 2_000m, sim.Renegotiated.TotalCost);
        Assert.Equal(sim.Current.TotalCost - sim.Renegotiated.TotalCost, sim.Saving);
        // The first rows save ~200 a month of interest: 2 000 of fees are covered by the 11th payment after the date.
        Assert.Equal(Sixtieth.PlusMonths(11), sim.BreakEvenOn);
        Assert.Equal(
            [new LoanPhase(1, PhaseMode.Annuity, 0.035m, RateType.Fixed, 60), new LoanPhase(2, PhaseMode.Annuity, 0.02m, RateType.Fixed)],
            sim.Terms.Phases);
        Assert.Equal(240, sim.Terms.TotalPayments);
    }

    [Fact]
    public void The_break_even_is_the_first_payment_whose_cumulated_saving_covers_the_fees()
    {
        var sim = Loans.Ok(LoanSimulations.Renegotiate(Loans.Golden(), Sixtieth, 0.02m, null, 2_000m));

        var before = Build(Loans.Golden()).Rows.Skip(60).ToList();
        var after = Build(sim.Terms).Rows.Skip(60).ToList();
        var saved = before.Zip(after, (b, a) => b.Interest + b.Insurance - a.Interest - a.Insurance).ToList();
        Assert.True(saved.Take(10).Sum() < 2_000m);
        Assert.True(saved.Take(11).Sum() >= 2_000m);
    }

    [Fact]
    public void Fees_above_the_saving_are_never_paid_back()
    {
        var sim = Loans.Ok(LoanSimulations.Renegotiate(Loans.Golden(), Sixtieth, 0.034m, null, 5_000m));

        Assert.True(sim.Saving < 0m);
        Assert.Null(sim.BreakEvenOn);
    }

    [Fact]
    public void A_longer_duration_lowers_the_payment_and_moves_the_end()
    {
        var sim = Loans.Ok(LoanSimulations.Renegotiate(Loans.Golden(), Sixtieth, 0.035m, 240, 0m));

        Assert.Equal((240, Sixtieth.PlusMonths(240), 300), (sim.Renegotiated.PaymentsLeft, sim.Renegotiated.EndDate, sim.Terms.TotalPayments));
        // The 60 payments made were an annuity over 240: it is imposed so they stay, the new phase computed over 300 − 60.
        Assert.Equal(
            [new LoanPhase(1, PhaseMode.Annuity, 0.035m, RateType.Fixed, 60, 1159.92m), new LoanPhase(2, PhaseMode.Annuity, 0.035m, RateType.Fixed)],
            sim.Terms.Phases);
        Assert.Equal(Build(Loans.Golden()).Rows.Take(60), Build(sim.Terms).Rows.Take(60));
        Assert.Empty(Build(sim.Terms).Warnings);
        Assert.True(sim.Renegotiated.Payment < sim.Current.Payment);
        Assert.True(sim.Saving < 0m); // more interest over a longer term
        Assert.Null(sim.BreakEvenOn);
    }

    [Fact]
    public void After_a_reduce_term_repayment_the_duration_asked_is_kept_by_imposing_the_annuity()
    {
        var repaid = Loans.Golden() with { Events = [new EarlyRepayment(Sixtieth, 10_000m, RepaymentEffect.ReduceTerm)] };
        var on = Sixtieth.PlusMonths(12);
        var left = Build(repaid).Rows.Count(r => r.DueDate > on);

        var sim = Loans.Ok(LoanSimulations.Renegotiate(repaid, on, 0.03m, left + 24, 0m));

        // The repayment's end (payment 226) is read over the count, so the count stays and the annuity over 178 is imposed: past
        // the count the loan runs on, its last row the cents the rounded annuity leaves.
        Assert.Equal((240, sim.Renegotiated.Payment), (sim.Terms.TotalPayments, sim.Terms.Phases[^1].PaymentOverride));
        var rows = Build(sim.Terms).Rows;
        Assert.Equal(left + 25, sim.Renegotiated.PaymentsLeft);
        Assert.True(rows[^1].Payment < 1m, rows[^1].ToString());
        Assert.Equal(Build(repaid).Rows.Take(72), rows.Take(72));
    }

    [Fact]
    public void A_deferral_running_at_the_date_ends_there()
    {
        var ptz = Loans.Annuity(40_000m, 0m, 240) with
        {
            Phases = [new LoanPhase(1, PhaseMode.DeferredTotal, 0m, RateType.Fixed, 60), new LoanPhase(2, PhaseMode.Annuity, 0m, RateType.Fixed)],
        };

        var sim = Loans.Ok(LoanSimulations.Renegotiate(ptz, Loans.First.PlusMonths(29), 0.01m, null, 0m));

        Assert.Equal(
            [new LoanPhase(1, PhaseMode.DeferredTotal, 0m, RateType.Fixed, 30), new LoanPhase(2, PhaseMode.Annuity, 0.01m, RateType.Fixed)],
            sim.Terms.Phases);
        Assert.Equal(210, sim.Renegotiated.PaymentsLeft);
    }

    [Theory]
    [InlineData("2044-01-15", 0.02, null, "on")]     // the last payment: nothing left after it
    [InlineData("2023-12-01", 0.02, null, "on")]     // before the disbursement
    [InlineData("2029-01-15", 1.0, null, "annualRate")]
    [InlineData("2029-01-15", 0.02, 0, "payments")]
    [InlineData("2029-01-15", 0.02, 541, "payments")] // 60 paid + 541 > 600
    public void A_renegotiation_the_engine_cannot_read_is_refused_at_its_field(string on, double rate, int? payments, string field)
    {
        var failure = Loans.Fail(LoanSimulations.Renegotiate(Loans.Golden(), LocalDate.FromDateTime(DateTime.Parse(on)), (decimal)rate, payments, 0m));

        Assert.Equal(field, Loans.FieldOf(failure));
    }

    private static LoanTerms Insured(params LoanInsurance[] insurances) => Loans.Golden() with { Insurances = insurances };

    private static readonly LoanInsurance OnInitial = new(InsuranceMode.RateOnInitial, null, 0.003m, Loans.First); // 50,00 a month

    [Fact]
    public void A_fixed_quote_against_a_rate_on_the_initial_capital_over_the_payments_left()
    {
        var on = Sixtieth.PlusDays(1); // the 61st payment is the first the quote pays
        var quote = new LoanInsurance(InsuranceMode.FixedMonthly, 30m, null, on);

        var c = Loans.Ok(LoanSimulations.CompareInsurance(Insured(OnInitial), on, [(0, quote)]));

        var line = Assert.Single(c.Lines);
        Assert.Equal((0, 180 * 50m, 180 * 30m), (line.Index, line.CurrentLeft, line.QuoteLeft));
        Assert.Equal((9_000m, 5_400m), (c.CurrentLeft, c.QuotedLeft));
        // The line stops at the 61st payment, where the quote starts: the engine charges each exactly once.
        var sixtyFirst = Sixtieth.PlusMonths(1);
        Assert.Equal([new ProposedInsurance(0, OnInitial with { EndsOn = sixtyFirst }), new ProposedInsurance(null, quote with { StartsOn = sixtyFirst })], c.Insurances);
    }

    [Fact]
    public void A_quote_on_the_remaining_capital_follows_the_balance_of_each_payment()
    {
        var on = Sixtieth.PlusDays(1);
        var quote = new LoanInsurance(InsuranceMode.RateOnRemaining, null, 0.002m, on);

        var line = Assert.Single(Loans.Ok(LoanSimulations.CompareInsurance(Insured(OnInitial), on, [(0, quote)])).Lines);

        // Each payment's premium is 0,2 % of the balance before it, over 12, to the cent: the 61st on 162 253,12.
        var rows = Build(Loans.Golden()).Rows;
        var expected = Enumerable.Range(60, 180).Sum(k => Math.Round(0.002m * rows[k - 1].Balance / 12m, 2, MidpointRounding.AwayFromZero));
        Assert.Equal(expected, line.QuoteLeft);
        Assert.True(line.QuoteLeft < 180 * 50m);
    }

    [Fact]
    public void Each_insured_person_is_compared_on_their_own_line()
    {
        var bob = new LoanInsurance(InsuranceMode.FixedMonthly, 20m, null, Loans.First);
        var quote = new LoanInsurance(InsuranceMode.RateOnInitial, null, 0.001m, Sixtieth); // 16,67 a month

        // On the 60th payment's day, that payment is made, as for a renegotiation: 180 are left.
        var c = Loans.Ok(LoanSimulations.CompareInsurance(Insured(OnInitial, bob), Sixtieth, [(1, quote)]));

        Assert.Equal([(0, 180 * 50m, (decimal?)null), (1, 180 * 20m, 180 * 16.67m)], c.Lines.Select(l => (l.Index, l.CurrentLeft, l.QuoteLeft)));
        Assert.Equal(180 * 50m + 180 * 16.67m, c.QuotedLeft);
        var sixtyFirst = Sixtieth.PlusMonths(1);
        Assert.Equal([new(0, OnInitial), new(1, bob with { EndsOn = sixtyFirst }), new ProposedInsurance(null, quote with { StartsOn = sixtyFirst })], c.Insurances);
    }

    [Fact]
    public void A_quote_names_a_running_line_once_and_is_read_as_a_contract_line()
    {
        var quote = new LoanInsurance(InsuranceMode.FixedMonthly, 30m, null, Sixtieth);
        var terms = Insured(OnInitial, OnInitial with { EndsOn = Loans.First.PlusMonths(12) });

        Assert.Equal("quotes[0].insurance", Loans.FieldOf(Loans.Fail(LoanSimulations.CompareInsurance(terms, Sixtieth, [(2, quote)]))));
        Assert.Equal("quotes[0].insurance", Loans.FieldOf(Loans.Fail(LoanSimulations.CompareInsurance(terms, Sixtieth, [(1, quote)]))));
        Assert.Equal("quotes[1].insurance", Loans.FieldOf(Loans.Fail(LoanSimulations.CompareInsurance(terms, Sixtieth, [(0, quote), (0, quote)]))));
        Assert.Equal("quotes[0].monthlyAmount",
            Loans.FieldOf(Loans.Fail(LoanSimulations.CompareInsurance(terms, Sixtieth, [(0, quote with { MonthlyAmount = null })]))));
        Assert.Equal("on", Loans.FieldOf(Loans.Fail(LoanSimulations.CompareInsurance(terms, new LocalDate(2044, 1, 16), [(0, quote)]))));
    }
}
