using System.Globalization;
using Nid.RealEstate.Application;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Tests;

/// <summary>Spec 3A2 §5: the views (list row, detail, ended), with the real engine on loans.md §4's golden case.</summary>
public class LoanReadingTests
{
    private static readonly LocalDate After60 = Loans.First.PlusMonths(59);

    private static LoanScheduleView ScheduleOf(LoanTerms terms) => LoanViews.ScheduleOf(terms, Loans.Ok(LoanScheduleBuilder.Build(terms)));

    [Fact]
    public void The_list_row_reads_the_golden_case_after_its_60th_payment()
    {
        var row = LoanViews.Summary(Loans.Item(), Loans.Stored(), After60, canWrite: true);

        Assert.Equal(("200000.00", "162253.12", "EUR"), (row.Principal, row.RemainingBalance, row.Currency));
        Assert.Equal((Loans.First.PlusMonths(60), "1159.92"), (row.NextPaymentOn!.Value, row.NextPaymentTotal));
        Assert.Equal(("0.035", new LocalDate(2044, 1, 15)), (row.CurrentRate, row.EndDate!.Value));
        Assert.Equal(("Prêt maison", "Banque", true, false), (row.Name, row.Lender, row.CanWrite, row.Ended));
        Assert.Equal("shared", row.Visibility);
    }

    [Fact]
    public void A_loan_ends_once_its_last_payment_and_its_last_repayment_are_past()
    {
        Assert.False(LoanViews.Summary(Loans.Item(), Loans.Stored(), new LocalDate(2044, 1, 15), true).Ended);
        Assert.True(LoanViews.Summary(Loans.Item(), Loans.Stored(), new LocalDate(2044, 1, 16), true).Ended);

        // A buy-out after the March 2030 payment: the rows stop on 2030-03-15, the loan on the repayment's own day.
        var march = Loans.Ok(LoanScheduleBuilder.Build(Loans.Golden())).Rows.Single(r => r.DueDate == new LocalDate(2030, 3, 15));
        var buyOut = new StoredRepayment(Guid.CreateVersion7(),
            new EarlyRepayment(new LocalDate(2030, 3, 20), march.Balance, RepaymentEffect.ReduceTerm), null, null, 1);
        var loan = Loans.Stored(repayments: [buyOut]);
        Assert.False(LoanViews.Summary(Loans.Item(), loan, new LocalDate(2030, 3, 17), true).Ended);
        Assert.False(LoanViews.Summary(Loans.Item(), loan, new LocalDate(2030, 3, 20), true).Ended);
        Assert.True(LoanViews.Summary(Loans.Item(), loan, new LocalDate(2030, 3, 21), true).Ended);
    }

    [Fact]
    public void The_detail_carries_the_contract_the_penalty_every_figure_and_the_warnings()
    {
        var file = new StoredFee(Guid.CreateVersion7(), new LoanFee(FeeKind.File, 1_000m, Loans.Disbursed), "dossier", 3);
        var penalty = new StoredFee(Guid.CreateVersion7(), new LoanFee(FeeKind.EarlyRepaymentPenalty, 300m, new LocalDate(2029, 2, 20)), null, 4);
        var repayment = new StoredRepayment(Guid.CreateVersion7(),
            new EarlyRepayment(new LocalDate(2029, 2, 20), 10_000m, RepaymentEffect.ReducePayment), penalty.Id, "prime", 5);
        var item = Loans.Item();

        var detail = LoanViews.Detail(item, Loans.Stored([file, penalty], [repayment]), After60, canWrite: false);

        var contract = detail.Contract;
        Assert.Equal(("200000.00", "half_up", "full", 240, 7u), (contract.Principal, contract.Rounding, contract.FirstPeriod, contract.TotalPayments, contract.Version));
        var phase = Assert.Single(contract.Phases);
        Assert.Equal((1, "annuity", "0.035", "fixed"), (phase.Seq, phase.Mode, phase.AnnualRate, phase.RateType));
        var borrower = Assert.Single(detail.Borrowers);
        Assert.Equal(("shared", item.Owners[0].PersonId.Value, 10_000), (detail.Visibility, borrower.PersonId, borrower.ShareBp));

        var r = Assert.Single(detail.Repayments);
        Assert.Equal((repayment.Id, "10000.00", "reduce_payment", "300.00", "prime", 5u), (r.Id, r.Amount, r.Effect, r.Penalty, r.Note, r.Version));
        Assert.Equal(new (Guid, Guid?)[] { (file.Id, null), (penalty.Id, repayment.Id) }, detail.Fees.Select(f => (f.Id, f.RepaymentId)));
        Assert.Equal("early_repayment_penalty", detail.Fees[1].Kind);

        var figures = detail.Figures;
        Assert.Equal(("162253.12", "31848.32", "1000.00", 180), (figures.Balance, figures.InterestPaid, figures.FeesPaid, figures.PaymentsLeft));
        Assert.Contains(new LoanWarningView(LoanWarnings.EventOffPaymentDate, 61), detail.Warnings);
        Assert.Equal((false, false), (detail.CanWrite, detail.Ended));
    }

    [Fact]
    public void The_schedule_and_a_simulation_read_as_text()
    {
        var schedule = ScheduleOf(Loans.Golden());
        Assert.Equal(240, schedule.Rows.Count);
        Assert.Equal(("1159.92", "583.33", "576.59", "0.035"), (schedule.Rows[0].Payment, schedule.Rows[0].Interest, schedule.Rows[0].Principal, schedule.Rows[0].AnnualRate));
        Assert.Equal(("162253.12", (string?)null), (schedule.Rows[59].Balance, schedule.Rows[59].EarlyRepayment));

        var simulation = LoanViews.SimulationOf(Loans.Ok(LoanMath.SimulateEarlyRepayment(Loans.Golden(), After60, 10_000m, null, 300m)));
        Assert.Equal(("10000.00", "300.00"), (simulation.Amount, simulation.Penalty));
        Assert.Equal(["reduce_term", "reduce_payment"], simulation.Outcomes.Select(o => o.Effect));
        Assert.Equal((226, "6566.58", "6266.58"), (simulation.Outcomes[0].PaymentsAfter, simulation.Outcomes[0].InterestSaved, simulation.Outcomes[0].NetSaving));
        Assert.Equal(("1159.92", "1088.43"), (simulation.Outcomes[1].PaymentBefore, simulation.Outcomes[1].PaymentAfter));
    }

    [Fact]
    public void Amounts_and_rates_go_on_the_wire_invariant_under_a_french_host() // Review Focus #3
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
        try
        {
            var row = LoanViews.Summary(Loans.Item(), Loans.Stored(), After60, true);
            Assert.Equal(("162253.12", "1159.92", "0.035"), (row.RemainingBalance, row.NextPaymentTotal, row.CurrentRate));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void The_schedule_totals_each_calendar_year_exactly() // spec 3B1 §4.1
    {
        var schedule = ScheduleOf(Loans.Golden());

        Assert.Equal(Enumerable.Range(2024, 21), schedule.Years.Select(y => y.Year));
        var first = schedule.Years[0]; // February to December 2024: eleven payments
        Assert.Equal(("12759.12", "6323.35", "6435.77", "0.00", "0.00", "193564.23"),
            (first.Payment, first.Interest, first.Principal, first.Insurance, first.EarlyRepayment, first.Balance));
        Assert.Equal(schedule.Rows[10].Balance, first.Balance);
        var last = schedule.Years[^1]; // January 2044: the last payment absorbs the rounding residue
        Assert.Equal(("1159.78", "3.37", "1156.41", "0.00"), (last.Payment, last.Interest, last.Principal, last.Balance));
        Assert.Equal(("78380.66", "200000.00"), (schedule.TotalInterest, schedule.TotalPrincipal));
    }

    [Fact]
    public void A_year_counts_its_early_repayment_and_its_insurance() // spec 3B1 §4.1
    {
        var terms = Loans.Golden() with
        {
            Insurances = [new LoanInsurance(InsuranceMode.FixedMonthly, 20m, null, Loans.First)],
            Events = [new EarlyRepayment(new LocalDate(2029, 2, 20), 10_000m, RepaymentEffect.ReducePayment)],
        };

        var schedule = ScheduleOf(terms);

        var y2029 = schedule.Years.Single(y => y.Year == 2029);
        Assert.Equal(("10000.00", "240.00"), (y2029.EarlyRepayment, y2029.Insurance));
        Assert.Equal(schedule.Rows.Last(r => r.DueDate.Year == 2029).Balance, y2029.Balance);
        Assert.Equal(("220.00", "0.00"), (schedule.Years[0].Insurance, schedule.Years[0].EarlyRepayment));
    }

    [Fact]
    public void A_repayment_before_the_first_payment_counts_in_its_own_year_even_without_a_row() // spec 3B2 §3
    {
        // Disbursed in December 2023, first payment in February 2024: one repayment in each calendar year before it.
        var terms = Loans.Golden() with
        {
            DisbursedOn = new LocalDate(2023, 12, 1),
            Events =
            [
                new EarlyRepayment(new LocalDate(2023, 12, 20), 10_000m, RepaymentEffect.ReduceTerm),
                new EarlyRepayment(new LocalDate(2024, 1, 20), 5_000m, RepaymentEffect.ReduceTerm),
            ],
        };

        var schedule = ScheduleOf(terms);

        var y2023 = schedule.Years[0];
        Assert.Equal((2023, "0.00", "0.00", "0.00", "0.00", "10000.00", "190000.00"),
            (y2023.Year, y2023.Payment, y2023.Interest, y2023.Principal, y2023.Insurance, y2023.EarlyRepayment, y2023.Balance));
        var y2024 = schedule.Years[1];
        Assert.Equal((2024, "5000.00"), (y2024.Year, y2024.EarlyRepayment));
        Assert.Equal(schedule.Rows.Last(r => r.DueDate.Year == 2024).Balance, y2024.Balance);
    }

    [Fact]
    public void An_off_date_repayment_counts_in_the_year_it_is_dated_and_the_year_before_ends_on_31_december() // spec 3B2 §3
    {
        // Dated 2030-01-05, the repayment applies right after the 2029-12-15 payment: that row's balance already deducts it.
        var terms = Loans.Golden() with { Events = [new EarlyRepayment(new LocalDate(2030, 1, 5), 10_000m, RepaymentEffect.ReduceTerm)] };
        var rows = Loans.Ok(LoanScheduleBuilder.Build(terms)).Rows;
        var december = rows.Single(r => r.DueDate == new LocalDate(2029, 12, 15));

        var schedule = ScheduleOf(terms);

        Assert.Equal(10_000m, december.EarlyRepayment); // the monthly rows keep the engine's reading
        var y2029 = schedule.Years.Single(y => y.Year == 2029);
        var y2030 = schedule.Years.Single(y => y.Year == 2030);
        Assert.Equal(("0.00", LoanReading.Amount(december.Balance + 10_000m)), (y2029.EarlyRepayment, y2029.Balance));
        Assert.Equal(("10000.00", LoanReading.Amount(rows.Last(r => r.DueDate.Year == 2030).Balance)), (y2030.EarlyRepayment, y2030.Balance));
    }

    [Fact]
    public void A_buy_out_dated_in_january_after_the_december_payment_makes_a_year_of_its_own() // spec 3B2 §3
    {
        var december = Loans.Ok(LoanScheduleBuilder.Build(Loans.Golden())).Rows.Single(r => r.DueDate == new LocalDate(2029, 12, 15));
        var terms = Loans.Golden() with { Events = [new EarlyRepayment(new LocalDate(2030, 1, 5), december.Balance, RepaymentEffect.ReduceTerm)] };

        var schedule = ScheduleOf(terms);

        Assert.Equal(new LocalDate(2029, 12, 15), Loans.Ok(LoanScheduleBuilder.Build(terms)).Rows[^1].DueDate);
        var last = schedule.Years[^1];
        Assert.Equal((2030, "0.00", LoanReading.Amount(december.Balance), "0.00"), (last.Year, last.Payment, last.EarlyRepayment, last.Balance));
        Assert.Equal(LoanReading.Amount(december.Balance), schedule.Years.Single(y => y.Year == 2029).Balance);
    }
}
