using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Tests;

/// <summary>Issue #409: the yearly tax calendar's dated rules.</summary>
public class TaxCalendarTests
{
    [Theory]
    [InlineData("01", 21)]
    [InlineData("19", 21)]
    [InlineData("2A", 28)]
    [InlineData("2B", 28)]
    [InlineData("54", 28)]
    [InlineData("55", 35)]
    [InlineData("95", 35)]
    [InlineData("971", 35)]
    public void The_2026_filing_deadline_follows_the_departments_zone(string department, int dayOfMay)
        => Assert.Equal(new LocalDate(2026, 5, 1).PlusDays(dayOfMay - 1), TaxCalendar.For(2026).FilingDeadline(department));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("20")]
    [InlineData("00")]
    [InlineData("96")]
    [InlineData("977")]
    [InlineData("2a")]
    public void An_unknown_department_has_no_deadline(string? department)
    {
        Assert.Null(TaxCalendar.For(2026).FilingDeadline(department));
        Assert.False(TaxCalendar.IsDepartment(department));
    }

    [Fact]
    public void Each_year_has_its_published_dates()
    {
        var y2025 = TaxCalendar.For(2025);
        Assert.Equal((new LocalDate(2025, 5, 22), new LocalDate(2025, 5, 28), new LocalDate(2025, 6, 5)), (y2025.FilingNorth, y2025.FilingCentre, y2025.FilingSouth));
        var y2026 = TaxCalendar.For(2026);
        Assert.Equal(new LocalDate(2026, 9, 1), y2026.NewRate);
        Assert.Equal(new LocalDate(2026, 9, 25), y2026.Balance);
        Assert.Equal(new LocalDate(2026, 10, 15), y2026.PropertyTax);
        Assert.False(y2026.CarriedForward);
    }

    [Fact]
    public void A_later_year_carries_the_last_dates_forward()
    {
        var c = TaxCalendar.For(2028);

        Assert.True(c.CarriedForward);
        Assert.Equal(2026, c.RulesYear);
        Assert.Equal(new LocalDate(2028, 5, 21), c.FilingNorth);
        Assert.Equal(new LocalDate(2028, 10, 15), c.PropertyTax);
    }
}
