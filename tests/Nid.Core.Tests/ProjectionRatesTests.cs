using Nid.Core.Contracts;
using Nid.Core.Domain.Projection;
using NodaTime;

namespace Nid.Core.Tests;

/// <summary>Inflation spec D5, D6, C16, C17: the yearly rates turned into monthly factors, and the savings that follow inflation.</summary>
public class ProjectionRatesTests
{
    private static readonly LocalDate Today = new(2026, 11, 6);
    private static readonly string[] Classes = [AllocationClasses.Cash, AllocationClasses.MoneyMarket];

    private static ProjectionAssumptions Long(decimal inflation = 0.02m) => ProjectionDefaults.Assumptions with { Inflation = inflation };

    /// <summary>The annual rate's monthly factor, (1 + r)^(1/12).</summary>
    private static double Monthly(double rate) => Math.Pow(1 + rate, 1.0 / 12);

    private static void Near(double expected, decimal actual) => Assert.True(Math.Abs((decimal)expected - actual) < 1e-12m, $"{expected} ≠ {actual}");

    [Fact]
    public void GridYears_start_with_next_month()
    {
        Assert.Equal(Enumerable.Range(2026, 11), ProjectionRates.GridYears(Today, 120));
        Assert.Equal([2027], ProjectionRates.GridYears(new LocalDate(2026, 12, 10), 12));
    }

    [Fact]
    public void InflationIn_prefers_the_typed_year_then_the_observed_current_year_then_the_long_term()
    {
        var a = Long(0.02m) with { InflationByYear = new Dictionary<int, decimal> { [2027] = 0.03m } };

        Assert.Equal(0.031m, ProjectionRates.InflationIn(a, 0.031m, Today, 2026));
        Assert.Equal(0.03m, ProjectionRates.InflationIn(a, 0.031m, Today, 2027));
        Assert.Equal(0.02m, ProjectionRates.InflationIn(a, 0.031m, Today, 2028));
        Assert.Equal(0.02m, ProjectionRates.InflationIn(a, null, Today, 2026));
    }

    [Fact]
    public void ClassRateIn_prefers_the_typed_year_then_the_long_term()
    {
        var a = Long() with
        {
            ClassRates = new Dictionary<string, decimal> { [AllocationClasses.Cash] = 0.02m, [AllocationClasses.Equity] = 0.06m },
            ClassRatesByYear = new Dictionary<string, IReadOnlyDictionary<int, decimal>>
            {
                [AllocationClasses.Cash] = new Dictionary<int, decimal> { [2027] = 0.025m },
            },
        };

        Assert.Equal(0.025m, ProjectionRates.ClassRateIn(a, AllocationClasses.Cash, 2027));
        Assert.Equal(0.02m, ProjectionRates.ClassRateIn(a, AllocationClasses.Cash, 2028));
        Assert.Equal(0.06m, ProjectionRates.ClassRateIn(a, AllocationClasses.Equity, 2027));
    }

    [Fact]
    public void Resolve_with_flat_rates_matches_the_old_compounding()
    {
        var rates = ProjectionRates.Resolve(Long(), null, Today, 120, Classes);

        Assert.Equal(1m, rates.Inflation[0]);
        for (var k = 0; k <= 120; k++)
        {
            Near(Math.Pow(1.02, k / 12.0), rates.Inflation[k]);
            Assert.Equal(rates.Inflation[k], rates.Income[k]);
        }
        for (var k = 1; k <= 120; k++)
            Assert.Equal((decimal)Monthly(0.02) - 1m, rates.MonthlyClassRate[AllocationClasses.Cash][k]);
    }

    [Fact]
    public void Resolve_uses_the_observed_rate_for_the_current_year()
    {
        var rates = ProjectionRates.Resolve(Long(), 0.031m, Today, 14, Classes);

        // k = 1 is December 2026, at the observed rate; k = 2 is January 2027, at the long-term rate.
        Near(Monthly(0.031), rates.Inflation[1]);
        Near(Monthly(0.031) * Monthly(0.02), rates.Inflation[2]);
        Near(Monthly(0.031) * 1.02, rates.Inflation[13]); // December 2027: one month at 3.1 %, the twelve of 2027 at 2 %
    }

    [Fact]
    public void Resolve_prefers_a_typed_current_year()
    {
        var typed = Long() with { InflationByYear = new Dictionary<int, decimal> { [2026] = 0.025m } };

        var rates = ProjectionRates.Resolve(typed, 0.031m, Today, 14, Classes);

        Near(Monthly(0.025), rates.Inflation[1]);
        Near(Monthly(0.025) * Monthly(0.02), rates.Inflation[2]);
    }

    [Fact]
    public void Resolve_ignores_past_years()
    {
        var stale = Long() with { InflationByYear = new Dictionary<int, decimal> { [2025] = 0.05m } };

        Assert.Equal(ProjectionRates.Resolve(Long(), null, Today, 24, Classes).Inflation, ProjectionRates.Resolve(stale, null, Today, 24, Classes).Inflation);
    }

    [Fact]
    public void Resolve_reads_yearly_cash_rates()
    {
        var yearly = Long() with
        {
            ClassRatesByYear = new Dictionary<string, IReadOnlyDictionary<int, decimal>> { [AllocationClasses.Cash] = new Dictionary<int, decimal> { [2027] = 0.03m } },
        };

        var rates = ProjectionRates.Resolve(yearly, null, Today, 14, Classes);

        var cash = rates.MonthlyClassRate[AllocationClasses.Cash];
        Assert.Equal((decimal)Monthly(0.02) - 1m, cash[1]); // December 2026: the long-term 2 %
        Assert.Equal((decimal)Monthly(0.03) - 1m, cash[2]); // January 2027
        Assert.Equal((decimal)Monthly(0.03) - 1m, cash[13]); // December 2027
        Assert.Equal((decimal)Monthly(0.02) - 1m, cash[14]); // January 2028
        Assert.Equal((decimal)Monthly(0.025) - 1m, rates.MonthlyClassRate[AllocationClasses.MoneyMarket][2]);
    }

    [Fact]
    public void Resolve_income_grows_by_inflation_plus_x()
    {
        var rates = ProjectionRates.Resolve(Long() with { IncomeGrowth = 0.01m }, null, Today, 12, Classes);

        Assert.Equal(1m, rates.Income[0]);
        Assert.Equal(rates.Inflation[12] * 1.01m, rates.Income[12]); // g(12) = i(12) × 1.01^(12/12)
    }

    [Fact]
    public void Savings_keep_their_purchasing_power_with_x_zero()
    {
        var assumptions = new ProjectionAssumptions(new Dictionary<string, decimal> { [AllocationClasses.Cash] = 0m }, 0.02m, null, AllocationClasses.Cash, true);
        var rates = ProjectionRates.Resolve(assumptions, null, Today, 24, [AllocationClasses.Cash]);
        // An income of 3,000 that keeps its purchasing power, as the caller indexes it: Revenu(k) = 3,000 × g(k).
        var income = rates.Income.Select(g => 3_000m * g).ToList();

        var points = HouseholdProjection.Project(new ProjectionInputs(Today, 24, [], income, [], 500m, assumptions, [], Rates: rates)).Points;

        // S(k) = 500 × i(k) + 3,000 × i(k) − 3,000 × i(k) = 500 × i(k); the pot earns nothing, so it holds Σ S(j) for j ≤ k.
        var pot = 0m;
        for (var k = 1; k <= 24; k++)
        {
            pot += 500m * rates.Inflation[k];
            Assert.Equal(Math.Round(pot, 2, MidpointRounding.AwayFromZero), points[k].Savings);
        }
    }
}
