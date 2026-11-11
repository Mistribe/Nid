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

    private static LoansOverview WithIncome(VisibilityScope scope, IncomePart income, params OverviewLoan[] loans)
        => LoansOverviewMath.Build(loans, Currency.Eur, Rates, Today, null, new OverviewIncome(income, scope, Alice));

    private static IReadOnlyDictionary<Currency, decimal> Eur(decimal amount) => new Dictionary<Currency, decimal> { [Currency.Eur] = amount };

    [Fact]
    public void The_debt_ratio_is_this_months_payments_over_the_income_with_the_room_under_35_percent()
    {
        var overview = WithIncome(VisibilityScope.Household, new IncomePart(2, Eur(5_000m), Eur(3_000m)), Home(), Car(), Ended());

        var ratio = overview.DebtRatio!;
        var payments = D(overview.MonthlyPayment);
        Assert.Equal((payments, 5_000m), (D(ratio.Payments), D(ratio.Income)));
        // The scenarios' rule, one implementation.
        Assert.Equal(Math.Round(DebtRatio.Of(payments, 5_000m)!.Value, 4).ToString("0.0000", CultureInfo.InvariantCulture), ratio.Ratio);
        Assert.Equal(("0.3500", 1_750m - payments, (string?)null), (ratio.Ceiling, D(ratio.Room), ratio.Unknown));
        Assert.Equal("under_ceiling", ratio.Reading);
    }

    [Fact]
    public void Payments_above_35_percent_of_the_income_read_over_the_ceiling_with_a_negative_room()
    {
        // The golden case's 1 159,92 a month over 3 000 is about 39 %.
        var ratio = WithIncome(VisibilityScope.Household, new IncomePart(1, Eur(3_000m)), Home()).DebtRatio!;

        Assert.Equal((1_050m - 1_159.92m, "over_ceiling"), (D(ratio.Room), ratio.Reading));
    }

    [Fact]
    public void Moi_reads_the_readers_shares_of_the_payments_and_of_the_income()
    {
        // Alice holds half of the golden case (1 159,92 a month) and the whole car.
        var overview = WithIncome(VisibilityScope.Me, new IncomePart(2, Eur(5_000m), Eur(2_000m)), Home(), Car());

        var ratio = overview.DebtRatio!;
        var car = D(overview.Loans.Single(l => l.Name == "Voiture").MonthlyPayment!);
        Assert.Equal((579.96m + car, 2_000m), (D(ratio.Payments), D(ratio.Income)));
        Assert.Equal(Math.Round((579.96m + car) / 2_000m, 4).ToString("0.0000", CultureInfo.InvariantCulture), ratio.Ratio);
        Assert.Equal(700m - 579.96m - car, D(ratio.Room));
    }

    [Theory]
    [InlineData(0)] // no source declared
    [InlineData(1)] // a source with nothing running today
    public void Without_income_the_ratio_is_unknown(int sources)
    {
        var overview = WithIncome(VisibilityScope.Household, new IncomePart(sources, sources == 0 ? new Dictionary<Currency, decimal>() : Eur(0m)), Home());

        var ratio = overview.DebtRatio!;
        Assert.Equal((null, null, "no_income", null), (ratio.Ratio, ratio.Room, ratio.Unknown, ratio.Reading));
        Assert.Equal(1_159.92m, D(ratio.Payments));
    }

    [Fact]
    public void An_income_without_a_rate_leaves_the_ratio_unknown()
    {
        var gbp = Currency.TryCreate("GBP", out var c) ? c : throw new InvalidOperationException();
        var overview = WithIncome(VisibilityScope.Household, new IncomePart(1, new Dictionary<Currency, decimal> { [gbp] = 4_000m }), Home());

        Assert.Equal((null, "fx_rate_missing"), (overview.DebtRatio!.Ratio, overview.DebtRatio.Unknown));
        Assert.Contains(overview.Findings, f => f.Code == FxCodes.RateMissing);
    }
}

/// <summary>Loans dashboard spec §4.3: the timeline's spans, the household's payment in steps, each person's share.</summary>
public class LoansOverviewTimelineTests
{
    private static readonly LocalDate Today = new(2026, 10, 21);
    private static readonly PersonId Alice = PersonId.New();
    private static readonly PersonId Bob = PersonId.New();
    private static readonly IReadOnlyList<PersonInfo> Household =
        [new(Bob, PersonKind.Adult, "Bob", null, 0), new(Alice, PersonKind.Adult, "Alice", null, 0)];

    private static OverviewLoan Loan(string name, LoanTerms terms, IReadOnlyList<StoredRepayment>? repayments = null, params (PersonId Person, int Bp)[] owners)
    {
        var shares = owners.Length == 0 ? [(Alice, 10_000)] : owners;
        var item = new ItemAccess(ItemId.New(), name, Visibility.Shared, shares.Select(o => new Owner(o.Person, PersonKind.Adult, o.Bp)).ToList())
        {
            Currency = "EUR",
        };
        return new OverviewLoan(item, Loans.Stored(repayments: repayments, terms: terms), null);
    }

    private static OverviewLoan Home() => Loan("Maison", Loans.Golden(), owners: [(Alice, 5_000), (Bob, 5_000)]);

    private static OverviewLoan Ptz() => Loan("PTZ", Loans.Annuity(40_000m, 0m, 240) with
    {
        Phases = [new LoanPhase(1, PhaseMode.DeferredTotal, 0m, RateType.Fixed, 60), new LoanPhase(2, PhaseMode.Annuity, 0m, RateType.Fixed)],
    });

    private static OverviewLoan Car() => Loan("Voiture", Loans.Annuity(12_000m, 0.05m, 36));

    private static LoansOverview Build(params OverviewLoan[] loans)
        => LoansOverviewMath.Build(loans, Currency.Eur, RateTable.Empty(Today, 7), Today, Household);

    private static decimal D(LoanMoneyView? money) => decimal.Parse(money!.Amount, CultureInfo.InvariantCulture);

    [Fact]
    public void A_deferred_ptz_spans_its_deferral_then_its_annuity()
    {
        var ptz = Ptz();

        var span = Assert.Single(Build(ptz).Spans);

        Assert.Equal((ptz.Item.Id, Loans.Disbursed, new LocalDate(2044, 1, 15)), (span.LoanId, span.Start, span.End));
        Assert.Equal(
            [
                (1, "deferred_total", Loans.First, new LocalDate(2029, 1, 15), "0", (string?)null),
                (2, "annuity", new LocalDate(2029, 2, 15), new LocalDate(2044, 1, 15), "0", "222.22"),
            ],
            span.Phases.Select(p => (p.Seq, p.Mode, p.From, p.To, p.AnnualRate, p.MonthlyPayment)));
    }

    [Fact]
    public void The_spans_follow_the_rows_order()
    {
        var overview = Build(Car(), Home());

        Assert.Equal(overview.Loans.Select(l => l.Id), overview.Spans.Select(s => s.LoanId));
    }

    [Fact]
    public void The_payment_steps_change_when_a_loan_ends_and_when_a_deferral_ends()
    {
        var overview = Build(Home(), Ptz(), Car());

        var car = Assert.Single(overview.Loans, l => l.Name == "Voiture").MonthlyPayment!;
        // The car's last payment absorbs its rounding (loans.md §3.2): a payment that changes is a step of its own (D3).
        Assert.Equal(
            [
                ("2026-10", "2026-12", 1159.92m + decimal.Parse(car, CultureInfo.InvariantCulture)),
                ("2027-01", "2027-01", 1519.59m),
                ("2027-02", "2029-01", 1159.92m),
            ],
            overview.Steps.Take(3).Select(s => (s.From, s.To, D(s.Amount))));
        Assert.Equal(("2029-02", 1159.92m + 222.22m), (overview.Steps[3].From, D(overview.Steps[3].Amount)));
        // The last payments absorb the rounding: the steps run on to the last month.
        Assert.Equal("2044-01", overview.Steps[^1].To);
        Assert.All(overview.Steps.Zip(overview.Steps.Skip(1)), p => Assert.NotEqual(p.First.Amount, p.Second.Amount));
    }

    [Fact]
    public void A_step_ends_when_an_insurance_stops()
    {
        var insured = Loan("Maison", Loans.Golden() with
        {
            Insurances = [new LoanInsurance(InsuranceMode.FixedMonthly, 30m, null, Loans.Disbursed, new LocalDate(2027, 6, 1))],
        });

        var steps = Build(insured).Steps;

        Assert.Equal(("2026-10", "2027-05", "1189.92"), (steps[0].From, steps[0].To, steps[0].Amount!.Amount));
        Assert.Equal(("2027-06", "1159.92"), (steps[1].From, steps[1].Amount!.Amount));
    }

    [Fact]
    public void A_repayment_that_reduces_the_payment_starts_a_lower_step()
    {
        var repaid = Loan("Maison", Loans.Golden(),
            repayments: [new StoredRepayment(Guid.NewGuid(), new EarlyRepayment(new LocalDate(2027, 3, 15), 50_000m, RepaymentEffect.ReducePayment), null, null, 1)]);

        var steps = Build(repaid).Steps;

        Assert.Equal(("2026-10", "2027-03", "1159.92"), (steps[0].From, steps[0].To, steps[0].Amount!.Amount));
        Assert.True(D(steps[1].Amount) < 1159.92m);
        Assert.Equal("2027-04", steps[1].From);
    }

    [Fact]
    public void Each_person_owns_their_share_of_each_loan_and_the_shares_add_up_to_the_totals()
    {
        var home = Home();
        var car = Car();

        var overview = Build(home, car);

        Assert.Equal([Bob.Value, Alice.Value], overview.ByPerson.Select(p => p.PersonId)); // the household's order
        var bob = overview.ByPerson[0];
        var alice = overview.ByPerson[1];
        Assert.Equal([(home.Item.Id, 5_000)], bob.Loans.Select(l => (l.LoanId, l.ShareBp)));
        Assert.Equal([(home.Item.Id, 5_000), (car.Item.Id, 10_000)], alice.Loans.Select(l => (l.LoanId, l.ShareBp)));
        Assert.Equal(D(overview.Balance), D(bob.Balance) + D(alice.Balance));
        Assert.Equal(D(overview.MonthlyPayment), D(bob.MonthlyPayment) + D(alice.MonthlyPayment));
        Assert.Equal(D(overview.Loans.Single(l => l.Id == home.Item.Id).BalanceConverted), D(bob.Loans[0].Balance) + D(alice.Loans[0].Balance));
        Assert.Equal(overview.Loans.Single(l => l.Id == car.Item.Id).BalanceConverted, alice.Loans[1].Balance);
    }

    [Fact]
    public void The_parts_of_a_loan_add_up_to_it_to_the_cent()
    {
        var split = Loan("Prêt", Loans.Annuity(1_000.01m, 0m, 1) with { FirstPaymentDate = new LocalDate(2027, 1, 15) }, owners: [(Alice, 5_000), (Bob, 5_000)]);

        var overview = Build(split);

        // Alice, first owner: 500.005 to the nearest cent; Bob, the last, what is left.
        Assert.Equal([(Bob.Value, "500.00"), (Alice.Value, "500.01")], overview.ByPerson.Select(p => (p.PersonId, p.Balance!.Amount)));
        Assert.Equal(1_000.01m, overview.ByPerson.Sum(p => D(p.Balance)));
    }
}
