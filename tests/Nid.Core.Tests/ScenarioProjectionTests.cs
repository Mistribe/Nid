using Nid.Core.Contracts;
using Nid.Core.Domain.Scenarios;
using NodaTime;

namespace Nid.Core.Tests;

/// <summary>Property purchase scenario spec §4.2: net worth month by month, without and with the purchase.</summary>
public class ScenarioProjectionTests
{
    private static readonly LocalDate Today = new(2026, 10, 22);
    private static readonly ItemId Envelope = new(Guid.NewGuid());

    private static PropertyPurchase Doc(LocalDate? purchaseOn = null, decimal price = 0m, decimal notary = 0m, decimal charges = 0m,
        IReadOnlyList<FundingLine>? funding = null, IReadOnlyList<ScenarioLoan>? loans = null, decimal cashRate = 0m,
        decimal monthlySavings = 0m, int horizon = 1) => new(
        1, PurchaseUse.Primary, purchaseOn ?? Today.PlusMonths(3), price, notary, 0m, 0m, charges, 0m, 0m, 0, null,
        funding ?? [], loans ?? [],
        new(horizon, new Dictionary<string, decimal> { [AllocationClasses.Cash] = cashRate, [AllocationClasses.RealEstate] = 0m },
            null, monthlySavings, AllocationClasses.Cash));

    private static ScenarioFacts Facts(decimal cash, IReadOnlyList<SimulatedLoan>? loans = null) => new(
        Today, 5_000m, 0m, 0m, [new(Envelope, AllocationClasses.Cash, AllocationLiquidity.Available, cash)],
        [new(new YearMonth(2026, 10), 0m, 0m, 0m)], loans ?? [], 0m);

    private static ProjectionResult Project(PropertyPurchase doc, ScenarioFacts facts) =>
        ScenarioCalculator.Project(doc, facts, ScenarioCalculator.Capacity(doc, facts));

    [Fact]
    public void Cash_grows_at_its_rate() =>
        Assert.Equal(10_200.00m, Project(Doc(cashRate: 0.02m), Facts(10_000m)).Points[12].Without);

    [Fact]
    public void Savings_go_to_their_class_each_month()
    {
        var points = Project(Doc(monthlySavings: 100m), Facts(10_000m)).Points;
        Assert.Equal(points[0].Without + 1_200m, points[12].Without);
    }

    [Fact]
    public void The_first_point_is_today_and_both_series_start_equal()
    {
        var first = Project(Doc(price: 200_000m, funding: [new(Envelope, 10_000m)]), Facts(10_000m)).Points[0];
        Assert.Equal(Today, first.On);
        Assert.Equal(first.Without, first.With);
        Assert.Equal(10_000m, first.Without);
    }

    [Fact]
    public void Buying_moves_the_down_payment_into_the_property_and_loses_the_fees()
    {
        var purchase = Today.PlusMonths(3);
        var loan = new SimulatedLoan("Main", Enumerable.Range(1, 240)
            .Select(n => new SimulatedPayment(n, purchase.PlusMonths(n), 1_000m, 0m, 750m, 0m, 180_000m - 750m * n)).ToList(), 0m);
        var doc = Doc(purchase, 200_000m, 15_000m, funding: [new(Envelope, 35_000m)],
            loans: [new("Main", 180_000m, 0m, 240, null, new(ScenarioInsuranceKind.FixedMonthly, 0m), 0m, 0m)]);
        var r = Project(doc, Facts(50_000m, [loan]));
        var bought = r.Points.Single(p => p.On == new LocalDate(2027, 1, 31));
        Assert.Equal(200_000m - 180_000m - 35_000m, bought.With - bought.Without);
        Assert.Equal(r.Points[3].With, r.Points[3].Without);   // the month before, nothing changed
    }

    [Fact]
    public void Savings_exhausted_names_the_first_month_below_zero()
    {
        var r = Project(Doc(Today, charges: 500m), Facts(1_000m));
        Assert.Equal(new YearMonth(2026, 12), r.SavingsExhaustedIn);
        Assert.Contains("savings_exhausted", r.Warnings);
    }

    [Fact]
    public void A_purchase_in_the_past_is_bought_today()
    {
        var r = Project(Doc(Today.PlusDays(-90), charges: 500m), Facts(10_000m));
        Assert.Contains("purchase_in_past", r.Warnings);
        Assert.Equal(12 + 1, r.Points.Count);
        Assert.Equal(Today, r.Points[0].On);
        var firstDiffering = r.Points.First(p => p.With != p.Without);
        Assert.Equal(new LocalDate(2026, 10, 31), firstDiffering.On);
        Assert.Equal(9_500m, firstDiffering.With);
    }

    [Fact]
    public void The_gap_at_the_horizon_is_with_minus_without()
    {
        var r = Project(Doc(Today, charges: 500m, horizon: 2), Facts(10_000m));
        Assert.Equal(10_000m, r.WithoutAtHorizon);
        Assert.Equal(10_000m - 24 * 500m, r.WithAtHorizon);
        Assert.Equal(r.WithAtHorizon - r.WithoutAtHorizon, r.Gap);
        Assert.Equal(r.Points[^1].With, r.WithAtHorizon);
    }
}
