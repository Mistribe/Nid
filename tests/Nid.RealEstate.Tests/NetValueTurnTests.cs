using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Tests;

/// <summary>Issue #406: since when a property's net value is negative, and when its loans fall below the last estimate held flat.</summary>
public class NetValueTurnTests
{
    private static readonly LocalDate Today = new(2026, 10, 22);
    private static readonly ItemId Home = ItemId.New();

    private static readonly PropertyDetails Bought =
        new(PropertyType.PrimaryResidence, Loans.Disbursed, 190_000m, 0m, 0m, 0m, null, null, PropertyCadence.Yearly, null, null, null);

    private static Valuation Estimate(LocalDate on, decimal amount) => new(Guid.NewGuid(), Home, on, Loans.Ok(Money.Create(amount, Currency.Eur)), null);

    /// <summary>The golden loan: 200 000 € at 3.5 % over 240 payments from 15 February 2024.</summary>
    private static readonly LoanTerms Terms = Loans.Golden();
    private static readonly LoanSchedule Schedule = Loans.Ok(LoanScheduleBuilder.Build(Terms));
    private static readonly TurnLoan Loan = new(Terms, Schedule, 1m);

    private static decimal Balance(LocalDate d) => LoanMath.At(Terms, Schedule, d).Balance;

    [Fact]
    public void Negative_since_the_estimate_that_made_it_so_and_positive_on_the_first_payment_below_the_last_estimate()
    {
        var turn = NetValueTurns.Of(Bought, [Estimate(new LocalDate(2024, 6, 1), 230_000m), Estimate(new LocalDate(2025, 6, 1), 160_000m)], [Loan], Today);

        Assert.NotNull(turn);
        Assert.Equal(new LocalDate(2025, 6, 1), turn.NegativeSince);
        var on = Assert.NotNull(turn.PositiveOn);
        var before = Schedule.Rows.Last(r => r.DueDate < on).DueDate;
        Assert.True(Balance(on) < 160_000m && Balance(before) >= 160_000m);
        Assert.Equal(15, on.Day);
    }

    [Fact]
    public void Negative_from_the_purchase_when_the_loan_exceeded_the_price()
    {
        var turn = NetValueTurns.Of(Bought, [Estimate(new LocalDate(2025, 6, 1), 160_000m)], [Loan], Today);

        Assert.Equal(Loans.Disbursed, turn!.NegativeSince);
    }

    [Fact]
    public void Beyond_the_loans_when_the_balance_never_falls_below_the_estimate()
    {
        var turn = NetValueTurns.Of(Bought, [Estimate(new LocalDate(2025, 6, 1), 0m)], [Loan], Today);

        Assert.Null(turn!.PositiveOn);
    }

    [Fact]
    public void Nothing_when_the_net_value_is_not_negative_or_unknown()
    {
        Assert.Null(NetValueTurns.Of(Bought, [Estimate(new LocalDate(2025, 6, 1), 400_000m)], [Loan], Today));
        Assert.Null(NetValueTurns.Of(Bought, [], [Loan], Today));
        Assert.Null(NetValueTurns.Of(Bought, [Estimate(new LocalDate(2025, 6, 1), 160_000m)], [], Today));
    }
}
