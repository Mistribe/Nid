using Nid.Core.Contracts;
using Nid.Core.Domain.Scenarios;
using NodaTime;

namespace Nid.Core.Tests;

/// <summary>Property purchase scenario spec §4.1: capacity at the retained month.</summary>
public class ScenarioCapacityTests
{
    private static readonly LocalDate Today = new(2026, 10, 22);
    private static readonly LocalDate Purchase = new(2026, 12, 1);

    private static PropertyPurchase Doc(PurchaseUse use = PurchaseUse.Primary, decimal charges = 200m, decimal currentRent = 1_100m,
        decimal expectedRent = 0m, int vacancy = 0, IReadOnlyList<FundingLine>? funding = null) => new(
        1, use, Purchase, 300_000m, 0m, 0m, 0m, charges, currentRent, expectedRent, vacancy, null,
        funding ?? [], [], new(10, new Dictionary<string, decimal>(), null, 0m, AllocationClasses.Cash));

    private static SimulatedLoan Loan(string name, int zeroRows, int rows, decimal payment, decimal insurance = 0m, decimal fees = 0m) =>
        new(name, Enumerable.Range(1, zeroRows + rows)
            .Select(n => new SimulatedPayment(n, Purchase.PlusMonths(n), n <= zeroRows ? 0m : payment, n <= zeroRows ? 0m : 100m,
                0m, insurance, 0m)).ToList(), fees);

    private static ScenarioFacts Facts(decimal? income, IReadOnlyList<SimulatedLoan> loans, decimal existingToday = 0m,
        decimal existingRetained = 0m, decimal tax = 0m, IReadOnlyList<SavingsSlice>? savings = null)
    {
        var retained = new YearMonth(2027, 1);
        return new(Today, income, 150m, 0m, savings ?? [],
            [new(new YearMonth(2026, 10), 0m, 0m, existingToday), new(retained, 0m, 0m, existingRetained)], loans, tax);
    }

    private static readonly SimulatedLoan Main = Loan("Main", 0, 240, 1_170.22m, 50m);

    [Fact]
    public void A_primary_home_with_one_loan()
    {
        var r = ScenarioCalculator.Capacity(Doc(), Facts(5_000m, [Main]));
        Assert.Equal(1_220.22m, r.NewPayment);
        Assert.Equal(0m, r.DebtRatioBefore);
        Assert.Equal(1_220.22m / 5_000m, r.DebtRatioAfter);
        Assert.Equal(4_850m, r.LeftBefore);
        Assert.Equal(4_529.78m, r.LeftAfter);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void A_ptz_in_total_deferral_retains_the_first_month_after_it()
    {
        var r = ScenarioCalculator.Capacity(Doc(), Facts(5_000m, [Main, Loan("PTZ", 60, 180, 277.78m)]));
        Assert.Equal(new YearMonth(2026, 12).PlusMonths(61), r.RetainedMonth);
        Assert.Equal(1_498.00m, r.NewPayment);
    }

    [Fact]
    public void A_rental_counts_seventy_percent_of_the_rent_in_the_ratio_and_the_net_rent_in_what_is_left()
    {
        var r = ScenarioCalculator.Capacity(Doc(PurchaseUse.Rental, expectedRent: 900m, vacancy: 1), Facts(5_000m, [Main], tax: 100m));
        Assert.Equal(1_220.22m / (5_000m + 630m), r.DebtRatioAfter);
        Assert.Equal(4_850m - 1_220.22m - 200m + 900m * 11 / 12 - 100m, r.LeftAfter);
        Assert.Equal(100m, r.MonthlyTaxIncrease);
    }

    [Fact]
    public void Above_35_percent_warns() =>
        Assert.Contains("debt_ratio_above_hcsf", ScenarioCalculator.Capacity(Doc(), Facts(3_000m, [Main])).Warnings);

    [Fact]
    public void Without_income_ratios_are_null()
    {
        var r = ScenarioCalculator.Capacity(Doc(), Facts(null, [Main]));
        Assert.Null(r.DebtRatioBefore);
        Assert.Null(r.DebtRatioAfter);
        Assert.Contains("no_income", r.Warnings);
        Assert.Equal(-150m - 1_220.22m - 200m + 1_100m, r.LeftAfter);
    }

    [Fact]
    public void An_existing_loan_ending_before_the_retained_month_no_longer_counts()
    {
        var r = ScenarioCalculator.Capacity(Doc(), Facts(5_000m, [Main], existingToday: 400m));
        Assert.Equal(400m / 5_000m, r.DebtRatioBefore);
        Assert.Equal(1_220.22m / 5_000m, r.DebtRatioAfter);
        Assert.Equal(4_450m, r.LeftBefore);
        Assert.Equal(4_450m - 1_220.22m - 200m + 1_100m + 400m, r.LeftAfter);
    }

    [Fact]
    public void Savings_after_the_down_payment_by_liquidity()
    {
        var a = new ItemId(Guid.NewGuid());
        var b = new ItemId(Guid.NewGuid());
        var savings = new SavingsSlice[] { new(a, AllocationClasses.Cash, "available", 20_000m), new(b, AllocationClasses.Cash, "days", 15_000m) };
        var r = ScenarioCalculator.Capacity(Doc(funding: [new(a, 10_000m)]), Facts(5_000m, [Main], savings: savings));
        Assert.Equal(new Dictionary<string, decimal> { ["available"] = 10_000m, ["days"] = 15_000m }, r.SavingsAfterByLiquidity);
    }

    [Fact]
    public void The_cost_of_the_loans_sums_interest_insurance_and_fees_and_the_last_payment_is_the_latest_due_date()
    {
        // A: 12 x (100 interest + 10 insurance) + 500 fees; B: 24 x (100 + 5) + 300 fees.
        var r = ScenarioCalculator.Capacity(Doc(), Facts(5_000m, [Loan("A", 0, 12, 100m, 10m, 500m), Loan("B", 0, 24, 100m, 5m, 300m)]));
        Assert.Equal(1_200m + 120m + 500m + 2_400m + 120m + 300m, r.LoansCost);
        Assert.Equal(Purchase.PlusMonths(24), r.LastPayment);
    }
}
