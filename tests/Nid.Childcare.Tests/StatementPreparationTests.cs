using Nid.Childcare.Contracts;
using Nid.Childcare.Domain;
using NodaTime;
using static Nid.Childcare.Tests.Fixture;

namespace Nid.Childcare.Tests;

public class StatementPreparationTests
{
    [Fact]
    public void Prepare_November_2026()
    {
        var days = StatementPreparation.Prepare(new YearMonth(2026, 11), Plan, Gaps);
        Assert.Equal(30, days.Count);
        int[] planned = [2, 3, 5, 6, 9, 10, 12, 13, 16, 17, 19, 20, 23, 24, 26, 27, 30];
        Assert.Equal(planned, days.Where(d => d.Planned).Select(d => d.Date.Day));
        Assert.All(days.Where(d => d.Planned), d =>
        {
            Assert.True(d.Present);
            Assert.Equal(Arrival, d.Arrival);
            Assert.Equal(Departure, d.Departure);
            Assert.True(d.Lunch);
            Assert.Null(d.Absence);
        });
        Assert.False(days.Single(d => d.Date == D(2026, 11, 11)).Planned);
    }

    [Fact]
    public void Prepare_fills_a_short_week_tuesday_until_16_45()
    {
        var days = StatementPreparation.Prepare(new YearMonth(2026, 11), Household(D(2026, 9, 1)), []);
        Assert.Equal(new LocalTime(16, 45), days.Single(d => d.Date == D(2026, 11, 3)).Departure);
        Assert.Equal(new LocalTime(18, 0), days.Single(d => d.Date == D(2026, 11, 10)).Departure);
    }

    [Fact]
    public void Holiday_on_a_planned_day()
    {
        var day = StatementPreparation.Prepare(new YearMonth(2027, 5), Plan, Gaps).Single(d => d.Date == D(2027, 5, 6));
        Assert.True(day.Planned);
        Assert.False(day.Present);
        Assert.Equal(AbsenceReason.Holiday, day.Absence);
    }

    [Fact]
    public void Gap_week_is_not_planned()
    {
        var days = StatementPreparation.Prepare(new YearMonth(2026, 12), Plan, [new CareGap(D(2026, 12, 21), 1)]);
        Assert.All(days.Where(d => d.Date.Day is 21 or 22 or 24 or 25), d => Assert.False(d.Planned));
    }
}
