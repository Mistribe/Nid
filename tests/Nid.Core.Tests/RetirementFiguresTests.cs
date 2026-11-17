using Nid.Core.Domain.Projection;
using NodaTime;

namespace Nid.Core.Tests;

/// <summary>Retirement spec §3: the retirement figures, pure.</summary>
public class RetirementFiguresTests
{
    private static readonly LocalDate On = new(2026, 11, 30);

    private static RetirementFigures? Of(decimal atRetirement, int? kR, int months = 12, decimal rate = 0.04m, decimal livingCost = 0m)
    {
        var income = Enumerable.Range(0, months + 1).Select(k => k < 12 ? 5_000m : atRetirement).ToList();
        var inflation = Enumerable.Range(0, months + 1).Select(k => k == 12 ? 1.02m : 1m).ToList();
        // The expenses at month 12 in today's money: the living cost plus 500 of commitments and loan payments.
        var independence = Enumerable.Range(0, months + 1)
            .Select(k => new IndependencePoint(On, k == 12 ? 204_000m : 0m, 0m, 0m, 0m, 0m, k == 12 ? livingCost + 500m : 0m)).ToList();
        return RetirementFigures.Of(income, inflation, independence, kR, rate, livingCost);
    }

    [Fact]
    public void The_gap_is_what_the_income_loses_in_todays_money()
    {
        var figures = Of(3_000m, 12)!;

        Assert.Equal(12, figures.Month);
        Assert.Equal(5_000m, figures.IncomeNow);
        Assert.Equal(3_000m / 1.02m, figures.IncomeAtRetirement);
        Assert.Equal(5_000m - 3_000m / 1.02m, figures.Gap);
        Assert.Equal(figures.Gap * 12m / 0.04m, figures.CapitalNeeded);
        Assert.Equal(200_000m, figures.CapitalAtRetirement);
    }

    [Fact]
    public void With_a_living_cost_the_gap_is_what_the_expenses_ask_beyond_the_income()
    {
        var figures = Of(3_060m, 12, livingCost: 4_000m)!;

        Assert.Equal(4_500m, figures.Need);
        Assert.Equal(3_000m, figures.IncomeAtRetirement);
        Assert.Equal(1_500m, figures.Gap);
        Assert.Equal(450_000m, figures.CapitalNeeded);
    }

    [Fact]
    public void A_higher_living_cost_asks_more_capital()
    {
        var low = Of(3_060m, 12, livingCost: 3_000m)!;
        var high = Of(3_060m, 12, livingCost: 3_500m)!;

        Assert.Equal((500m, 1_000m), (low.Gap, high.Gap));
        Assert.True(high.CapitalNeeded > low.CapitalNeeded);
    }

    [Fact]
    public void An_income_covering_the_expenses_needs_nothing()
    {
        var figures = Of(6_000m, 12, livingCost: 4_000m)!;

        Assert.Equal((0m, 0m), (figures.Gap, figures.CapitalNeeded));
    }

    [Fact]
    public void Without_a_living_cost_there_is_no_need()
    {
        Assert.Null(Of(3_000m, 12)!.Need);
    }

    [Fact]
    public void No_retirement_month_is_no_figures()
    {
        Assert.Null(Of(3_000m, null));
        Assert.Null(Of(3_000m, 0));
        Assert.Null(Of(3_000m, 13));
    }

    [Fact]
    public void An_income_above_the_present_one_needs_nothing()
    {
        var figures = Of(6_000m, 12)!;

        Assert.Equal((0m, 0m), (figures.Gap, figures.CapitalNeeded));
    }
}
