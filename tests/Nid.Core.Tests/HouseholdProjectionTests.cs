using Nid.Core.Contracts;
using Nid.Core.Domain.Projection;
using Nid.Core.Domain.Scenarios;
using NodaTime;

namespace Nid.Core.Tests;

/// <summary>Projection spec §3.3: the household net worth month by month.</summary>
public class HouseholdProjectionTests
{
    private static readonly LocalDate Today = new(2026, 10, 27);
    private static readonly ItemId Envelope = new(Guid.NewGuid());

    private static ProjectionAssumptions Assumptions(decimal cashRate = 0m, decimal inflation = 0m, bool freed = true) => new(
        new Dictionary<string, decimal> { [AllocationClasses.Cash] = cashRate }, inflation, null, AllocationClasses.Cash, freed);

    private static SavingsSlice Cash(decimal amount) => new(Envelope, AllocationClasses.Cash, AllocationLiquidity.Available, amount);

    private static IReadOnlyList<RealEstateMonth> NoDebt(int months, Func<int, decimal>? payments = null) =>
        Enumerable.Range(0, months + 1)
            .Select(k => new RealEstateMonth(new YearMonth(2026, 10).PlusMonths(k), 0m, 0m, payments?.Invoke(k) ?? 0m)).ToList();

    private static HouseholdProjectionResult Run(
        decimal cash, int months = 12, decimal savings = 0m, ProjectionAssumptions? assumptions = null,
        IReadOnlyList<decimal>? income = null, IReadOnlyList<RealEstateMonth>? realEstate = null,
        IReadOnlyList<ProjectionMilestone>? milestones = null) =>
        HouseholdProjection.Project(new(
            Today, months, [Cash(cash)], income ?? Enumerable.Repeat(0m, months + 1).ToList(), realEstate ?? [],
            savings, assumptions ?? Assumptions(), milestones ?? []));

    [Fact]
    public void First_point_is_today()
    {
        var result = Run(10_000m, realEstate: [new(new YearMonth(2026, 10), 300_000m, 200_000m, 1_000m), .. NoDebt(12).Skip(1)]);
        Assert.Equal(new HouseholdPoint(Today, 110_000m, 110_000m, 10_000m, 300_000m, 200_000m, 10_000m, 300_000m, 200_000m), result.Points[0]);
    }

    [Fact]
    public void Cash_compounds_monthly_to_the_annual_rate()
    {
        var points = Run(10_000m, assumptions: Assumptions(cashRate: 0.02m)).Points;
        Assert.Equal(10_200.00m, points[12].Savings);
        Assert.Equal(new LocalDate(2027, 10, 31), points[12].On);
    }

    [Fact]
    public void Monthly_savings_go_to_the_savings_class() =>
        Assert.Equal(1_200m, Run(0m, savings: 100m).Points[12].Savings);

    [Fact]
    public void Real_divides_by_cumulated_inflation()
    {
        var point = Run(10_000m, assumptions: Assumptions(inflation: 0.02m)).Points[12];
        Assert.Equal(9_803.92m, point.Real);
        Assert.Equal(10_000m, point.Nominal);
    }

    [Fact]
    public void Real_parts_divide_by_the_same_inflation()
    {
        var realEstate = Enumerable.Range(0, 13)
            .Select(k => new RealEstateMonth(new YearMonth(2026, 10).PlusMonths(k), 300_000m, 200_000m, 0m)).ToList();
        var point = Run(10_000m, assumptions: Assumptions(inflation: 0.02m), realEstate: realEstate).Points[12];
        Assert.Equal((10_000m, 300_000m, 200_000m), (point.Savings, point.Property, point.Debt));
        Assert.Equal((9_803.92m, 294_117.65m, 196_078.43m), (point.SavingsReal, point.PropertyReal, point.DebtReal));
        Assert.Equal(107_843.14m, point.Real);
    }

    [Fact]
    public void Income_rise_adds_to_savings()
    {
        var income = new List<decimal> { 2_000m, 2_000m }.Concat(Enumerable.Repeat(2_500m, 11)).ToList();
        Assert.Equal(1_000m, Run(0m, income: income).Points[3].Savings);
    }

    [Fact]
    public void Freed_loan_payment_goes_to_savings()
    {
        var realEstate = NoDebt(12, k => k < 2 ? 1_000m : 0m);
        Assert.Equal(2_000m, Run(0m, realEstate: realEstate).Points[3].Savings);
        Assert.Equal(0m, Run(0m, realEstate: realEstate, assumptions: Assumptions(freed: false)).Points[3].Savings);
    }

    [Fact]
    public void Debt_and_property_follow_the_real_estate_months()
    {
        var realEstate = Enumerable.Range(0, 13)
            .Select(k => new RealEstateMonth(new YearMonth(2026, 10).PlusMonths(k), 300_000m + k * 1_000m, 200_000m - k * 500m, 1_000m)).ToList();
        var points = Run(0m, realEstate: realEstate).Points;
        for (var k = 0; k <= 12; k++)
        {
            Assert.Equal(realEstate[k].Debt, points[k].Debt);
            Assert.Equal(realEstate[k].PropertyValue, points[k].Property);
        }
    }

    [Fact]
    public void Negative_savings_drain_cash_and_warn()
    {
        var result = Run(500m, savings: -200m);
        Assert.Equal(new YearMonth(2027, 1), result.SavingsExhaustedIn);
        Assert.Contains(result.Milestones, m => m.Kind == "savings_exhausted" && m.On == new LocalDate(2027, 1, 31));
    }

    [Fact]
    public void Milestones_outside_the_grid_are_dropped()
    {
        var result = Run(0m, milestones:
        [
            new(new LocalDate(2060, 1, 1), "loan_end", "far", null),
            new(new LocalDate(2027, 3, 15), "loan_end", "near", null),
        ]);
        var kept = Assert.Single(result.Milestones);
        Assert.Equal(new LocalDate(2027, 3, 15), kept.On);
    }
}
