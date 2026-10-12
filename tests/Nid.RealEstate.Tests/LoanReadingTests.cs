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

    [Fact]
    public void The_list_row_reads_the_golden_case_after_its_60th_payment()
    {
        var row = LoanViews.Summary(Loans.Item(), Loans.Stored(), After60, canWrite: true);

        Assert.Equal(("200000.00", "162253.12", "EUR"), (row.Principal, row.RemainingBalance, row.Currency));
        Assert.Equal((Loans.First.PlusMonths(60), "1159.92"), (row.NextPaymentOn!.Value, row.NextPaymentTotal));
        Assert.Equal(("0.035", new LocalDate(2044, 1, 15)), (row.CurrentRate, row.EndDate!.Value));
        Assert.Equal(("Prêt maison", "Banque", true, false), (row.Name, row.Lender, row.CanWrite, row.Ended));
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
        var schedule = LoanViews.ScheduleOf(Loans.Ok(LoanScheduleBuilder.Build(Loans.Golden())));
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
}
