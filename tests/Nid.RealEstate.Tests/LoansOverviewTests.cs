using System.Globalization;
using Nid.Core.Contracts;
using Nid.RealEstate.Application;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Tests;

/// <summary>Loans dashboard spec §3.4: the overview's figures on a fixture household, with the real engine.</summary>
public class LoansOverviewTests
{
    private static readonly LocalDate Today = new(2026, 10, 21);
    private static readonly PersonId Alice = PersonId.New();
    private static readonly PersonId Bob = PersonId.New();

    /// <summary>1 USD = 0.9 EUR today.</summary>
    private static readonly RateTable Rates = new([new FxRate(Today, Currency.Eur, 0.9m, FxRateSource.Ecb)], Today, 7);

    private static OverviewLoan Loan(string name, LoanTerms terms, string currency = "EUR", IReadOnlyList<StoredRepayment>? repayments = null,
        params (PersonId Person, int Bp)[] owners)
    {
        var shares = owners.Length == 0 ? [(Alice, 10_000)] : owners;
        var item = new ItemAccess(ItemId.New(), name, Visibility.Shared, shares.Select(o => new Owner(o.Person, PersonKind.Adult, o.Bp)).ToList())
        {
            Currency = currency,
        };
        return new OverviewLoan(item, Loans.Stored(repayments: repayments, terms: terms with { Currency = Currency.TryCreate(currency, out var c) ? c : Currency.Eur }), null);
    }

    /// <summary>The golden case, shared 50/50: 1 159,92 a month until January 2044.</summary>
    private static OverviewLoan Home() => Loan("Maison", Loans.Golden(), owners: [(Alice, 5_000), (Bob, 5_000)]);

    /// <summary>A zero-rate loan in total deferral for five years, then repaid over the fifteen after.</summary>
    private static OverviewLoan Ptz() => Loan("PTZ", Loans.Annuity(40_000m, 0m, 240) with
    {
        Phases = [new LoanPhase(1, PhaseMode.DeferredTotal, 0m, RateType.Fixed, 60), new LoanPhase(2, PhaseMode.Annuity, 0m, RateType.Fixed)],
    });

    /// <summary>36 payments from February 2024: its last on 15 January 2027.</summary>
    private static OverviewLoan Car() => Loan("Voiture", Loans.Annuity(12_000m, 0.05m, 36));

    /// <summary>12 payments, the last on 15 January 2025: ended.</summary>
    private static OverviewLoan Ended() => Loan("Travaux", Loans.Annuity(5_000m, 0.02m, 12));

    private static OverviewLoan Dollars() => Loan("Studio", Loans.Annuity(30_000m, 0.04m, 120), "USD");

    private static LoansOverview Build(params OverviewLoan[] loans) => LoansOverviewMath.Build(loans, Currency.Eur, Rates, Today);

    private static LoanFigures Figures(OverviewLoan loan)
    {
        var terms = LoanReading.Terms(Currency.TryCreate(loan.Item.Currency!, out var c) ? c : Currency.Eur, loan.Loan.Contract, loan.Loan.Fees, loan.Loan.Repayments);
        return LoanMath.At(terms, LoanReading.Schedule(terms, loan.Loan), Today);
    }

    private static decimal D(string amount) => decimal.Parse(amount, CultureInfo.InvariantCulture);

    private static decimal D(LoanMoneyView? money) => D(money!.Amount);

    [Fact]
    public void The_household_figures_add_up_the_running_loans_figures_today()
    {
        OverviewLoan[] loans = [Home(), Ptz(), Car(), Ended()];

        var overview = Build(loans);

        var running = loans.Take(3).Select(Figures).ToList();
        Assert.Equal((3, 1), (overview.RunningCount, overview.EndedCount));
        Assert.Equal("EUR", overview.Currency);
        Assert.Equal(running.Sum(f => f.Balance), D(overview.Balance));
        Assert.Equal(252_000m, D(overview.Principal));
        Assert.Equal(running.Sum(f => f.PrincipalRepaid), D(overview.PrincipalRepaid));
        Assert.Equal(running.Sum(f => f.CostPaid), D(overview.CostPaid));
        Assert.Equal(running.Sum(f => f.CostRemaining), D(overview.CostRemaining));
        Assert.Equal(Math.Round(D(overview.PrincipalRepaid) / 252_000m, 4).ToString("0.0000", CultureInfo.InvariantCulture), overview.RepaidShare);
        Assert.Equal(new LocalDate(2044, 1, 15), overview.FreeOn);
        // D3: each loan's October payment, insurance included; the PTZ's is 0 in its deferral.
        Assert.Equal(D(overview.Loans.Single(l => l.Name == "Maison").MonthlyPayment!) + D(overview.Loans.Single(l => l.Name == "Voiture").MonthlyPayment!),
            D(overview.MonthlyPayment));
        Assert.Equal("0.00", overview.Loans.Single(l => l.Name == "PTZ").MonthlyPayment);
        Assert.Equal(new LocalDate(2026, 11, 15), overview.NextPaymentOn);
        Assert.Equal(running.Sum(f => f.NextPaymentTotal ?? 0m), D(overview.NextPaymentTotal));
        Assert.Empty(overview.Findings);
        Assert.False(overview.ConvertedIsEstimate);
    }

    [Fact]
    public void An_ended_loan_is_in_no_figure_and_no_point()
    {
        var ended = Ended();

        var overview = Build(Home(), ended);

        Assert.DoesNotContain(overview.Loans, l => l.Id == ended.Item.Id);
        Assert.DoesNotContain(overview.Months.SelectMany(m => m.ByLoan), b => b.LoanId == ended.Item.Id);
        Assert.DoesNotContain(overview.Events, e => e.LoanId == ended.Item.Id);
        Assert.Equal(200_000m, D(overview.Principal));
    }

    [Fact]
    public void Only_ended_loans_leave_an_empty_overview_that_counts_them()
    {
        var overview = Build(Ended());

        Assert.Equal((0, 1), (overview.RunningCount, overview.EndedCount));
        Assert.Equal(("0.00", null, null), (overview.Balance!.Amount, overview.FreeOn, overview.RepaidShare));
        Assert.Empty(overview.Months);
        Assert.Empty(overview.Events);
    }

    [Fact]
    public void The_rows_come_by_balance_largest_first_with_their_own_figures()
    {
        var overview = Build(Car(), Ptz(), Home());

        Assert.Equal(["Maison", "PTZ", "Voiture"], overview.Loans.Select(l => l.Name));
        var home = overview.Loans[0];
        var figures = Figures(Home());
        Assert.Equal((LoanReading.Amount(figures.Balance), "200000.00", "1159.92", "0.035", new LocalDate(2044, 1, 15)),
            (home.Balance, home.Principal, home.MonthlyPayment, home.CurrentRate, home.EndDate));
        Assert.Equal(Math.Round(figures.PrincipalRepaid / 200_000m, 4).ToString("0.0000", CultureInfo.InvariantCulture), home.RepaidShare);
        Assert.Equal((home.Balance, "EUR"), (home.BalanceConverted!.Amount, home.BalanceConverted.Currency));
        Assert.Equal([5_000, 5_000], home.Owners.Select(o => o.ShareBp));
    }

    [Fact]
    public void The_first_point_is_todays_balance_and_the_curve_runs_to_the_last_payment()
    {
        var overview = Build(Home(), Ptz(), Car());

        Assert.Equal("2026-10", overview.Months[0].Month);
        Assert.Equal(overview.Balance, overview.Months[0].Total);
        Assert.Equal("2044-01", overview.Months[^1].Month);
        Assert.Equal(12 * 17 + 4, overview.Months.Count); // October 2026 to January 2044
        // The total is the sum of the lines it is drawn with, every month.
        Assert.All(overview.Months, m => Assert.Equal(m.ByLoan.Sum(b => D(b.Balance)), D(m.Total)));
        Assert.Equal("0.00", overview.Months[^1].Total!.Amount);
    }

    [Fact]
    public void A_later_point_is_the_balance_after_the_last_payment_due_in_its_month()
    {
        var home = Home();
        var schedule = LoanScheduleBuilder.Build(Loans.Golden()).TryGetValue(out var s) ? s! : throw new InvalidOperationException();

        var overview = Build(home);

        var march = overview.Months.Single(m => m.Month == "2030-03");
        Assert.Equal(LoanReading.Amount(schedule.Rows.Single(r => r.DueDate == new LocalDate(2030, 3, 15)).Balance), march.Total!.Amount);
    }

    [Fact]
    public void A_settled_loan_leaves_the_points_after_its_last_payment()
    {
        var car = Car();

        var overview = Build(Home(), car);

        var january = overview.Months.Single(m => m.Month == "2027-01");
        Assert.Equal("0.00", january.ByLoan.Single(b => b.LoanId == car.Item.Id).Balance!.Amount);
        Assert.DoesNotContain(overview.Months.Single(m => m.Month == "2027-02").ByLoan, b => b.LoanId == car.Item.Id);
    }

    [Fact]
    public void The_events_are_the_phase_ends_and_last_payments_with_the_households_payment_the_month_after()
    {
        var home = Home();
        var ptz = Ptz();
        var car = Car();

        var overview = Build(home, ptz, car);

        Assert.Equal(
            [
                (new LocalDate(2027, 1, 15), LoanOverviewCodes.LoanEnds, car.Item.Id, (int?)null),
                (new LocalDate(2029, 2, 15), LoanOverviewCodes.PhaseEnds, ptz.Item.Id, 1),
                // A tie keeps the rows' order: the larger balance first.
                (new LocalDate(2044, 1, 15), LoanOverviewCodes.LoanEnds, home.Item.Id, null),
                (new LocalDate(2044, 1, 15), LoanOverviewCodes.LoanEnds, ptz.Item.Id, null),
            ],
            overview.Events.Select(e => (e.Date, e.Code, e.LoanId, e.PhaseSeq)));
        // February 2027: the house alone, the PTZ still deferred; March 2029: the house and the PTZ's 40 000 / 180.
        Assert.Equal("1159.92", overview.Events[0].MonthlyPaymentAfter!.Amount);
        Assert.Equal(1159.92m + 222.22m, D(overview.Events[1].MonthlyPaymentAfter));
        Assert.Equal("0.00", overview.Events[^1].MonthlyPaymentAfter!.Amount);
    }

    [Fact]
    public void A_repayment_that_reduces_the_payment_lowers_this_months_payment()
    {
        var repaid = Loan("Maison", Loans.Golden(),
            repayments: [new StoredRepayment(Guid.NewGuid(), new EarlyRepayment(new LocalDate(2025, 6, 15), 50_000m, RepaymentEffect.ReducePayment), null, null, 1)]);

        var overview = Build(repaid);

        Assert.True(D(overview.MonthlyPayment) < 1159.92m);
        Assert.Equal(overview.Loans[0].MonthlyPayment, overview.MonthlyPayment!.Amount);
        Assert.Equal(new LocalDate(2044, 1, 15), overview.FreeOn);
    }

    [Fact]
    public void A_loan_in_another_currency_keeps_its_amounts_and_converts_at_todays_rate_as_an_estimate_ahead()
    {
        var dollars = Dollars();

        var overview = Build(Home(), dollars);

        var row = overview.Loans.Single(l => l.Id == dollars.Item.Id);
        var figures = Figures(dollars);
        Assert.Equal(("USD", LoanReading.Amount(figures.Balance)), (row.Currency, row.Balance));
        Assert.Equal(LoanReading.Amount(Math.Round(figures.Balance * 0.9m, 2, MidpointRounding.AwayFromZero)), row.BalanceConverted!.Amount);
        Assert.Equal(Figures(Home()).Balance + D(row.BalanceConverted), D(overview.Balance));
        Assert.True(overview.ConvertedIsEstimate);
    }

    [Fact]
    public void A_missing_rate_leaves_the_household_figures_unknown_and_says_so()
    {
        var dollars = Dollars();

        var overview = LoansOverviewMath.Build([Home(), dollars], Currency.Eur, RateTable.Empty(Today, 7), Today);

        Assert.Null(overview.Balance);
        Assert.Null(overview.MonthlyPayment);
        Assert.Null(overview.RepaidShare);
        Assert.Null(overview.Months[0].Total);
        var row = overview.Loans.Single(l => l.Id == dollars.Item.Id);
        Assert.Null(row.BalanceConverted);
        Assert.NotNull(row.Balance); // its own amount stays
        Assert.Equal(FxCodes.RateMissing, Assert.Single(overview.Findings).Code);
        Assert.Equal(dollars.Item.Id, overview.Loans[^1].Id); // an unknown balance sorts last
    }

    [Fact]
    public void A_loan_whose_bank_table_is_the_reference_reads_the_banks_balance()
    {
        var golden = LoanScheduleBuilder.Build(Loans.Golden()).TryGetValue(out var s) ? s! : throw new InvalidOperationException();
        var table = golden.Rows.Where(r => r.DueDate <= Today.PlusMonths(2))
            .Select(r => new StoredBankRow(Guid.NewGuid(), new BankRow(r.DueDate, r.Payment, r.Interest, r.Principal, 30m, r.Balance - 100m))).ToList();
        var home = Home();
        var bank = home with { Loan = home.Loan with { Source = ScheduleSource.Bank, Bank = table } };

        var overview = Build(bank);

        Assert.Equal(LoanViews.Summary(bank.Item, bank.Loan, Today, true).RemainingBalance, overview.Balance!.Amount);
        Assert.Equal("1189.92", overview.MonthlyPayment!.Amount); // the bank's insurance
    }
}
