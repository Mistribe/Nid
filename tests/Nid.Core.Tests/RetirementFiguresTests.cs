using Nid.Core.Domain.Projection;
using NodaTime;

namespace Nid.Core.Tests;

/// <summary>Retirement spec §3: the retirement figures, pure.</summary>
public class RetirementFiguresTests
{
    private static readonly LocalDate On = new(2026, 11, 30);

    private static RetirementFigures? Of(decimal atRetirement, int? kR, int months = 12, decimal rate = 0.04m)
    {
        var income = Enumerable.Range(0, months + 1).Select(k => k < 12 ? 5_000m : atRetirement).ToList();
        var inflation = Enumerable.Range(0, months + 1).Select(k => k == 12 ? 1.02m : 1m).ToList();
        var independence = Enumerable.Range(0, months + 1).Select(k => new IndependencePoint(On, k == 12 ? 204_000m : 0m, 0m, 0m, 0m, 0m, 0m)).ToList();
        return RetirementFigures.Of(income, inflation, independence, kR, rate);
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
