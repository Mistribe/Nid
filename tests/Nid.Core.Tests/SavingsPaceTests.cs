using Nid.Core.Application.Dashboard;

namespace Nid.Core.Tests;

/// <summary>Issue #81: the savings pace, the last 3 months' monthly average against the last 12 months'.</summary>
public class SavingsPaceTests
{
    [Fact]
    public void A_drop_beyond_the_threshold_gives_both_averages()
        // 12 000 a year is 1 000 a month; 2 000 over 3 months is 666.67, a third below.
        => Assert.Equal((2_000m / 3, 1_000m), SavingsPace.Dropped(2_000m, 12_000m, 30m));

    [Theory]
    [InlineData(2_100, 12_000)] // 700 a month: exactly 30 % below, not more
    [InlineData(3_000, 12_000)] // the same pace
    [InlineData(0, 0)]          // nothing saved in the year: no pace to fall from
    [InlineData(-500, -1_200)]  // a year of withdrawals
    public void No_drop_beyond_the_threshold_raises_nothing(double recent, double year)
        => Assert.Null(SavingsPace.Dropped((decimal)recent, (decimal)year, 30m));

    [Fact]
    public void A_missing_total_raises_nothing()
    {
        Assert.Null(SavingsPace.Dropped(null, 12_000m, 30m));
        Assert.Null(SavingsPace.Dropped(0m, null, 30m));
    }
}
