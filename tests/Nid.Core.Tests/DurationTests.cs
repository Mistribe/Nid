using NodaTime;
using Nid.Core.Contracts;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Core.Tests;

public class DurationTests
{
    private static Duration OneMonth => Duration.Create(1, DurationUnit.Month).Ok();

    [Fact]
    public void Count_must_be_positive()
    {
        Assert.True(Duration.Create(0, DurationUnit.Month).IsFailure);
        Assert.True(Duration.Create(-1, DurationUnit.Day).IsFailure);
    }

    [Fact]
    public void Month_occurrences_are_computed_from_the_anchor_not_month_by_month()
    {
        var anchor = new LocalDate(2024, 1, 31);
        Assert.Equal(new LocalDate(2024, 2, 29), OneMonth.AddTo(anchor, 1));
        Assert.Equal(new LocalDate(2024, 3, 31), OneMonth.AddTo(anchor, 2));
        Assert.Equal(new LocalDate(2024, 4, 30), OneMonth.AddTo(anchor, 3));
    }

    [Fact]
    public void Non_leap_year_clamps_to_28()
        => Assert.Equal(new LocalDate(2025, 2, 28), OneMonth.AddTo(new LocalDate(2025, 1, 31), 1));

    [Fact]
    public void Multi_month_duration_multiplies_the_occurrence()
    {
        var quarter = Duration.Create(3, DurationUnit.Month).Ok();
        Assert.Equal(new LocalDate(2025, 7, 31), quarter.AddTo(new LocalDate(2025, 1, 31), 2));
    }

    [Fact]
    public void Day_week_and_year_units()
    {
        var start = new LocalDate(2025, 1, 1);
        Assert.Equal(new LocalDate(2025, 1, 11), Duration.Create(10, DurationUnit.Day).Ok().AddTo(start, 1));
        Assert.Equal(new LocalDate(2025, 1, 8), Duration.Create(1, DurationUnit.Week).Ok().AddTo(start, 1));
        Assert.Equal(new LocalDate(2027, 1, 1), Duration.Create(1, DurationUnit.Year).Ok().AddTo(start, 2));
    }
}

public class DateRangeTests
{
    private static LocalDate D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void End_is_the_first_day_not_covered()
    {
        var range = DateRange.Create(D(2025, 1, 1), D(2025, 2, 1)).Ok();
        Assert.True(range.Contains(D(2025, 1, 31)));
        Assert.False(range.Contains(D(2025, 2, 1)));
    }

    [Fact]
    public void Empty_or_reversed_range_is_refused()
    {
        Assert.True(DateRange.Create(D(2025, 1, 1), D(2025, 1, 1)).IsFailure);
        Assert.True(DateRange.Create(D(2025, 2, 1), D(2025, 1, 1)).IsFailure);
    }

    [Fact]
    public void Adjacent_ranges_do_not_overlap()
    {
        var a = DateRange.Create(D(2025, 1, 1), D(2025, 2, 1)).Ok();
        var b = DateRange.Create(D(2025, 2, 1), D(2025, 3, 1)).Ok();
        Assert.False(a.Overlaps(b));
        Assert.False(b.Overlaps(a));
    }

    [Fact]
    public void Open_ended_range_overlaps_anything_after_its_start()
    {
        var open = DateRange.Create(D(2025, 1, 1), null).Ok();
        var later = DateRange.Create(D(2030, 1, 1), D(2030, 2, 1)).Ok();
        Assert.True(open.Overlaps(later));
        Assert.True(open.Contains(D(2099, 1, 1)));
    }
}
