using Nid.Banking.Domain;
using NodaTime;

namespace Nid.Banking.Tests;

/// <summary>Issue #305: a category above its usual month by the threshold, or whose budget is or will be over (a 30-day month, 50 %).</summary>
public class UnusualSpendingTests
{
    private static readonly LocalDate Day10 = new(2026, 4, 10);

    private static string? Usual(LocalDate today, decimal spent, decimal usual) => UnusualSpending.Find(today, 50m, spent, usual, null, null);

    [Fact]
    public void Above_the_usual_month_by_the_threshold_and_the_minimum_excess_stands_out()
    {
        Assert.Equal(UnusualSpending.AboveUsual, Usual(Day10, 300.01m, 200m));
        Assert.Null(Usual(Day10, 300m, 200m)); // exactly 50 % above
        Assert.Null(UnusualSpending.Find(Day10, 100m, 300.01m, 200m, null, null)); // the threshold is the reader's
        Assert.Null(Usual(Day10, 150m, 200m)); // below the usual month
    }

    [Fact]
    public void A_small_excess_says_nothing_however_far_above_the_usual_month()
    {
        Assert.Null(Usual(Day10, 60m, 10m)); // 500 % above, 50 more
        Assert.Equal(UnusualSpending.AboveUsual, Usual(Day10, 60.01m, 10m));
        Assert.Equal(UnusualSpending.AboveUsual, Usual(Day10, 80m, 0m)); // nothing spent there before
        Assert.Null(UnusualSpending.Find(Day10, 50m, 900m, null, null, null)); // no usual month to compare with
    }

    [Fact]
    public void The_usual_month_and_the_pace_wait_for_the_tenth()
    {
        Assert.Null(Usual(Day10.PlusDays(-1), 900m, 200m));
        Assert.Equal(UnusualSpending.AboveUsual, Usual(Day10, 900m, 200m));
        Assert.Null(UnusualSpending.Find(Day10.PlusDays(-1), 50m, 250m, null, 300m, 30m));
    }

    [Fact]
    public void A_budget_over_stands_out_any_day()
    {
        Assert.Equal(UnusualSpending.BudgetOver, UnusualSpending.Find(new LocalDate(2026, 4, 2), 50m, 300.01m, null, 300m, 20m));
        Assert.Null(UnusualSpending.Find(new LocalDate(2026, 4, 2), 50m, 300m, null, 300m, 20m));
    }

    [Fact]
    public void A_budget_over_at_the_current_pace_stands_out()
    {
        // Day 10 of 30: 100 expected, 120 spent; 20 ahead grows to 60 by the month's end.
        Assert.Equal(360m, UnusualSpending.Projected(300m, 100m, 120m, Day10));
        Assert.Equal(UnusualSpending.BudgetPace, UnusualSpending.Find(Day10, 50m, 120m, null, 300m, 100m));
        // 15 ahead: 45 over at the end, under the minimum excess.
        Assert.Null(UnusualSpending.Find(Day10, 50m, 115m, null, 300m, 100m));
        // A rent committed whole on the 5th: 900 of 1 000 expected already, 920 spent stays on track.
        Assert.Null(UnusualSpending.Find(Day10, 50m, 920m, null, 1_000m, 933.33m));
    }
}
