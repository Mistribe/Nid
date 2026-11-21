using Nid.Core.Contracts;
using Nid.Core.Application.Dashboard;

namespace Nid.Core.Tests;

/// <summary>Issue #81: the net worth milestone, a multiple of the step reached since the previous month-end.</summary>
public class NetWorthMilestoneTests
{
    [Theory]
    [InlineData(49_999.99, 50_000, 50_000)] // reaching the step exactly crosses it
    [InlineData(48_000, 52_000, 50_000)]
    [InlineData(90_000, 210_000, 200_000)] // several steps at once: the highest
    public void Crossing_gives_the_highest_step_reached(double before, double now, double crossed)
        => Assert.Equal(Decimals.FromDouble(crossed), NetWorthMilestone.Crossed(Decimals.FromDouble(before), Decimals.FromDouble(now), 50_000m));

    [Theory]
    [InlineData(50_000, 99_999)] // already above the step at the month-end
    [InlineData(10_000, 49_999.99)] // not reached yet
    [InlineData(120_000, 60_000)] // going down crosses nothing
    [InlineData(-20_000, 10_000)] // zero is not a milestone
    public void No_crossing_raises_nothing(double before, double now)
        => Assert.Null(NetWorthMilestone.Crossed(Decimals.FromDouble(before), Decimals.FromDouble(now), 50_000m));

    [Fact]
    public void A_missing_figure_raises_nothing()
    {
        Assert.Null(NetWorthMilestone.Crossed(null, 60_000m, 50_000m));
        Assert.Null(NetWorthMilestone.Crossed(40_000m, null, 50_000m));
    }
}
