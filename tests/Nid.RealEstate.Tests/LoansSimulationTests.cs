using System.Globalization;
using Nid.Core.Contracts;
using Nid.RealEstate.Application;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Tests;

/// <summary>Loans dashboard spec §5.4: an early repayment split across the household's loans, with the real engine.</summary>
public class LoansSimulationTests
{
    private static readonly LocalDate Today = new(2026, 10, 21);
    private static readonly LocalDate On = new(2026, 11, 15);
    private static readonly RateTable Rates = new([new FxRate(Today, Currency.Eur, 0.9m, FxRateSource.Ecb)], Today, 7);

    private static OverviewLoan Loan(string name, LoanTerms terms, string currency = "EUR")
    {
        var item = new ItemAccess(ItemId.New(), name, Visibility.Shared, [new Owner(PersonId.New(), PersonKind.Adult, 10_000)]) { Currency = currency };
        return new OverviewLoan(item, Loans.Stored(terms: terms with { Currency = Currency.TryCreate(currency, out var c) ? c : Currency.Eur }), null);
    }

    private static OverviewLoan Home() => Loan("Maison", Loans.Golden());

    private static OverviewLoan Ptz() => Loan("PTZ", Loans.Annuity(40_000m, 0m, 240) with
    {
        Phases = [new LoanPhase(1, PhaseMode.DeferredTotal, 0m, RateType.Fixed, 60), new LoanPhase(2, PhaseMode.Annuity, 0m, RateType.Fixed)],
    });

    /// <summary>5 % over 36 payments: 3 000-odd left in November 2026.</summary>
    private static OverviewLoan Car() => Loan("Voiture", Loans.Annuity(12_000m, 0.05m, 36));

    private static SimulateLoansQuery Query(decimal amount, SplitRule split = SplitRule.HighestRate, ItemId? loan = null,
        RepaymentEffect effect = RepaymentEffect.ReduceTerm, LocalDate? on = null, IReadOnlyList<LoanPenalty>? penalties = null)
        => new(VisibilityScope.Household, on ?? On, amount, split, loan, effect, penalties ?? []);

    private static LoansSimulation Ok(OverviewLoan[] loans, SimulateLoansQuery query, IReadOnlySet<ItemId>? writable = null)
        => Loans.Ok(LoansOverviewMath.Simulate(loans, Currency.Eur, Rates, Today, query, writable ?? loans.Select(l => l.Item.Id).ToHashSet()));

    private static (string? Field, string Code) Refused(OverviewLoan[] loans, SimulateLoansQuery query)
    {
        var failure = Loans.Fail(LoansOverviewMath.Simulate(loans, Currency.Eur, Rates, Today, query, new HashSet<ItemId>()));
        return (Loans.FieldOf(failure), failure.Code);
    }

    private static decimal D(string amount) => decimal.Parse(amount, CultureInfo.InvariantCulture);

    private static decimal CarBalanceOn()
    {
        var terms = Loans.Annuity(12_000m, 0.05m, 36);
        return LoanMath.At(terms, Loans.Ok(LoanScheduleBuilder.Build(terms)), On).Balance;
    }

    [Fact]
    public void Highest_rate_settles_the_car_then_puts_the_rest_on_the_house_and_nothing_on_the_ptz()
    {
        OverviewLoan[] loans = [Home(), Ptz(), Car()];

        var result = Ok(loans, Query(10_000m));

        Assert.Equal(["Voiture", "Maison", "PTZ"], result.Loans.Select(l => loans.Single(x => x.Item.Id == l.LoanId).Item.DisplayName));
        var car = result.Loans[0];
        Assert.Equal((LoanReading.Amount(CarBalanceOn()), true, "0.05"), (car.Amount, car.Settled, car.Rate));
        Assert.Equal(On, car.EndAfter);
        Assert.Equal(LoanReading.Amount(10_000m - CarBalanceOn()), result.Loans[1].Amount);
        Assert.Equal(("0.00", false, "0"), (result.Loans[2].Amount, result.Loans[2].Settled, result.Loans[2].Rate));
        Assert.Equal(("2026-11-15", "10000.00", "highest_rate", "reduce_term"),
            (result.Asked.On.ToString("yyyy-MM-dd", null), result.Asked.Amount, result.Asked.Split, result.Asked.Effect));
    }

    [Fact]
    public void The_household_net_saving_is_the_sum_of_the_loans_and_the_penalties_are_deducted()
    {
        OverviewLoan[] loans = [Home(), Car()];
        var car = loans[1].Item.Id;

        var result = Ok(loans, Query(10_000m, penalties: [new LoanPenalty(car, 120m)]));

        Assert.Equal(result.Loans.Sum(l => D(l.NetSaving)), D(result.NetSaving.Amount));
        Assert.Equal(result.Loans.Sum(l => D(l.InterestSaved)), D(result.InterestSaved.Amount));
        Assert.Equal("120.00", result.Penalties.Amount);
        Assert.Equal(D(result.InterestSaved.Amount) - 120m, D(result.NetSaving.Amount));
        Assert.True(D(result.InterestSaved.Amount) > 0m);
    }

    [Fact]
    public void Reducing_the_payment_lowers_the_households_payment_the_month_after()
    {
        OverviewLoan[] loans = [Home()];

        var result = Ok(loans, Query(50_000m, effect: RepaymentEffect.ReducePayment));

        Assert.Equal(("2026-12", "1159.92"), (result.PaymentMonth, result.MonthlyPaymentBefore!.Amount));
        Assert.True(D(result.MonthlyPaymentAfter!.Amount) < 1159.92m);
        Assert.Equal(result.FreeOnBefore, result.FreeOnAfter);
        Assert.Equal(result.Loans[0].PaymentAfter, result.MonthlyPaymentAfter.Amount);
    }

    [Fact]
    public void Reducing_the_term_brings_the_households_last_payment_forward()
    {
        var result = Ok([Home()], Query(50_000m));

        Assert.True(result.FreeOnAfter < result.FreeOnBefore);
        Assert.Equal(result.MonthlyPaymentBefore, result.MonthlyPaymentAfter);
        Assert.Equal("2026-10", result.Months[0].Month);
        Assert.Equal(result.Months[0].Before, result.Months[0].After); // the repayment comes in November
        Assert.Equal(D(result.Months[1].Before!.Amount) - 50_000m, D(result.Months[1].After!.Amount));
        Assert.Equal("0.00", result.Months[^1].After!.Amount);
    }

    [Fact]
    public void Chosen_puts_everything_on_one_loan_even_a_lower_rate()
    {
        OverviewLoan[] loans = [Home(), Car()];

        var result = Ok(loans, Query(5_000m, SplitRule.Chosen, loans[0].Item.Id));

        Assert.Equal((loans[0].Item.Id, "5000.00"), (result.Loans[0].LoanId, result.Loans[0].Amount));
        Assert.Equal("0.00", result.Loans[1].Amount);
        Assert.Equal(loans[0].Item.Id, result.Asked.LoanId);
    }

    [Fact]
    public void A_loan_in_another_currency_takes_nothing_and_says_why()
    {
        var dollars = Loan("Studio", Loans.Annuity(30_000m, 0.09m, 120), "USD");

        var result = Ok([Home(), dollars], Query(10_000m));

        var row = result.Loans.Single(l => l.LoanId == dollars.Item.Id);
        Assert.Equal((LoanSimulationCodes.OtherCurrency, "0.00"), (row.Code, row.Amount));
        Assert.Equal("10000.00", result.Loans[0].Amount); // the higher dollar rate is not split across
        Assert.Equal(dollars.Item.Id, result.Loans[^1].LoanId);
    }

    [Fact]
    public void A_penalty_for_a_loan_that_takes_nothing_is_refused_at_its_loan()
    {
        OverviewLoan[] loans = [Home(), Ptz()];

        Assert.Equal(("penalties[0].loanId", LoanSimulationCodes.PenaltyWithoutRepayment),
            Refused(loans, Query(1_000m, penalties: [new LoanPenalty(loans[1].Item.Id, 50m)])));
    }

    [Fact]
    public void An_amount_above_the_balances_is_refused()
    {
        OverviewLoan[] loans = [Car()];

        Assert.Equal(("amount", LoanCodes.EventAboveBalance), Refused(loans, Query(CarBalanceOn() + 0.01m)));
    }

    [Fact]
    public void The_chosen_loans_balance_bounds_the_amount()
    {
        var car = Car();

        Assert.Equal(("amount", LoanCodes.EventAboveBalance), Refused([Home(), car], Query(CarBalanceOn() + 1m, SplitRule.Chosen, car.Item.Id)));
    }

    [Fact]
    public void A_past_date_or_one_after_the_last_payment_is_refused()
    {
        Assert.Equal(("on", LoanCodes.InvalidLoan), Refused([Home()], Query(1_000m, on: Today.PlusDays(-1))));
        Assert.Equal(("on", LoanCodes.InvalidLoan), Refused([Home()], Query(1_000m, on: new LocalDate(2044, 2, 1))));
    }

    [Fact]
    public void A_chosen_loan_outside_the_household_is_refused_at_loanId()
    {
        Assert.Equal(("loanId", LoanCodes.InvalidLoan), Refused([Home()], Query(1_000m, SplitRule.Chosen, ItemId.New())));
    }

    [Fact]
    public void A_loan_the_reader_cannot_change_is_simulated_but_not_writable()
    {
        OverviewLoan[] loans = [Home()];

        var result = Ok(loans, Query(1_000m), writable: new HashSet<ItemId>());

        Assert.False(result.Loans[0].CanWrite);
        Assert.Equal("1000.00", result.Loans[0].Amount);
    }

    [Fact]
    public void A_loan_whose_bank_table_is_the_reference_is_simulated_on_it()
    {
        var golden = Loans.Ok(LoanScheduleBuilder.Build(Loans.Golden()));
        var table = golden.Rows.Where(r => r.DueDate <= Today)
            .Select(r => new StoredBankRow(Guid.NewGuid(), new BankRow(r.DueDate, r.Payment, r.Interest, r.Principal, 30m, r.Balance - 100m))).ToList();
        var home = Home();
        var bank = home with { Loan = home.Loan with { Source = ScheduleSource.Bank, Bank = table } };

        var result = Ok([bank], Query(10_000m, effect: RepaymentEffect.ReducePayment));

        // Today's point is the bank's balance (100 less than the calculation), and the months after go on from it, with and without.
        Assert.Equal(LoanViews.Summary(bank.Item, bank.Loan, Today, true).RemainingBalance, result.Months[0].Before!.Amount);
        var computedNovember = golden.Rows.Single(r => r.DueDate == On).Balance;
        Assert.True(D(result.Months[1].Before!.Amount) < computedNovember);
        Assert.Equal(D(result.Months[1].Before!.Amount) - 10_000m, D(result.Months[1].After!.Amount));
        Assert.True(D(result.MonthlyPaymentAfter!.Amount) < D(result.MonthlyPaymentBefore!.Amount));
    }
}
