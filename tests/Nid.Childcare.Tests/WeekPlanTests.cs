using Nid.Childcare.Contracts;
using NodaTime;
using static Nid.Childcare.Tests.Fixture;

namespace Nid.Childcare.Tests;

public class WeekPlanTests
{
    private static readonly LocalDate Start = D(2026, 9, 2);

    [Fact]
    public void Rotation_alternates_from_the_week_the_arrangement_starts()
    {
        var plan = Household(Start);
        Assert.Equal(0, plan.TypeOf(D(2026, 8, 31)));
        Assert.Equal(1, plan.TypeOf(D(2026, 9, 7)));
        Assert.Equal(0, plan.TypeOf(D(2026, 9, 14)));
    }

    [Fact]
    public void An_override_wins_over_the_rotation()
    {
        var plan = Household(Start) with { Overrides = [new WeekOverride(D(2026, 9, 14), 1)] };
        Assert.Equal(1, plan.TypeOf(D(2026, 9, 16)));
        Assert.Equal(1, plan.TypeOf(D(2026, 9, 21)));
    }

    [Fact]
    public void Days_before_the_anchor_rotate_backwards() => Assert.Equal(1, Household(Start).TypeOf(D(2026, 8, 24)));

    [Fact]
    public void SlotOn_reads_the_day_of_its_week_type()
    {
        var plan = Household(Start);
        Assert.Equal(new LocalTime(16, 45), plan.SlotOn(D(2026, 9, 8))!.Departure);
        Assert.Null(plan.SlotOn(D(2026, 9, 9)));
        Assert.Equal(0m, plan.HoursOn(D(2026, 9, 9)));
    }
}
