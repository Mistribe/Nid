using NodaTime;
using Nid.Core.Contracts;
using Nid.Subscriptions.Application;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using static Nid.Subscriptions.Tests.TestData;

namespace Nid.Subscriptions.Tests;

public class FxConversionTests
{
    private static readonly LocalDate Day = new(2025, 3, 3);
    private static readonly LocalDate Today = new(2025, 10, 15);

    private static RateTable Table(params (LocalDate On, decimal EurPerUsd)[] rates)
        => new(rates.Select(r => new FxRate(r.On, Currency.Eur, r.EurPerUsd, FxRateSource.Ecb)), Today, 7);

    private static CostPiece Usd(decimal amount, LocalDate? on = null, bool projected = false)
        => new(on ?? Day, Currency.Usd, amount, projected);

    private static CostPiece Euros(decimal amount) => new(Day, Currency.Eur, amount, false);

    [Fact]
    public void Pieces_in_the_reference_currency_need_no_rate()
    {
        Assert.Null(FxConversion.Need([Euros(1m), Euros(2m)], Currency.Eur));

        var total = FxConversion.Convert([Euros(1m), Euros(2m)], RateTable.Empty(Today, 7), Currency.Eur);
        Assert.Equal((3m, false), (total.Total!.Value.Amount, total.IsEstimate));
    }

    [Fact]
    public void The_need_covers_every_currency_and_date()
    {
        var need = FxConversion.Need([Usd(1m, Day), Usd(1m, Day.PlusDays(40)), Euros(1m)], Currency.Eur)!;
        // xUnit 2.9.3's Assert.Equal on a ValueTuple compares elements via EqualityComparer<T>.Default
        // (reference equality for List<T>) rather than structurally, so the collection is asserted on its own.
        Assert.Equal([Currency.Eur, Currency.Usd], need.Quotes.OrderBy(c => c.Code).ToList());
        Assert.Equal((Day, Day.PlusDays(40)), (need.From, need.To));
    }

    [Fact]
    public void Each_piece_converts_at_the_rate_of_its_own_date()
    {
        var table = Table((Day, 0.9m), (Day.PlusDays(30), 0.8m));

        var total = FxConversion.Convert([Usd(10m, Day), Usd(10m, Day.PlusDays(30)), Euros(1m)], table, Currency.Eur);

        Assert.Equal(18m, total.Total!.Value.Amount); // 9 + 8 + 1
        Assert.Empty(total.Missing);
    }

    [Fact]
    public void One_missing_rate_leaves_no_total_and_one_finding_per_currency_and_date()
    {
        var table = Table((Day, 0.9m));

        var total = FxConversion.Convert([Usd(10m, Day), Usd(5m, Day.PlusDays(20)), Usd(5m, Day.PlusDays(20))], table, Currency.Eur);

        Assert.Null(total.Total);
        var missing = Assert.Single(total.Missing);
        Assert.Equal((FxCodes.RateMissing, "EUR:2025-03-23"), (missing.Code, missing.Reference));
    }

    [Fact]
    public void A_projected_foreign_piece_makes_the_total_an_estimate()
    {
        var table = Table((Today.PlusDays(-1), 0.9m));
        var total = FxConversion.Convert([Usd(10m, Today.PlusMonths(2), projected: true)], table, Currency.Eur);
        Assert.Equal((9m, true), (total.Total!.Value.Amount, total.IsEstimate));
    }

    [Fact]
    public void The_converted_total_is_rounded_once_to_four_decimals()
    {
        var table = Table((Day, 0.33333m));
        var total = FxConversion.Convert([Usd(0.0001m), Usd(0.0001m), Usd(0.0001m)], table, Currency.Eur);
        Assert.Equal(0.0001m, total.Total!.Value.Amount); // 3 × 0.000033333 = 0.000099999, rounded once
    }

    [Fact]
    public void Household_cash_pieces_count_a_shared_purchase_once()
    {
        var shared = PurchaseId.New();
        var second = ItemId.New();
        Timeline Build(ItemId item) => SubscriptionTimeline.Build(
            new SubscriptionInput(new SubscriptionHeader(item, true, null), [],
                [new Purchase(shared, Day, "bundle", Money.Create(20m, Currency.Usd).Ok(), Household, PurchaseKind.PrepaidTerm,
                    [new Grant(Sub, Months(1), CoverageSource.Paid, null), new Grant(second, Months(1), CoverageSource.Paid, null)], null)]),
            Today, Today).Ok();
        var window = DateRange.Create(Day, Day.PlusDays(1)).Ok();

        var cash = HouseholdCost.CashPieces([Build(Sub), Build(second)], window);

        Assert.Equal(20m, Assert.Single(cash).Amount);
    }
}
