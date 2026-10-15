using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using NodaTime.Text;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.RealEstate.Tests;

/// <summary>Spec 3E2 §2: what the routine proposes for a month — a planned cost's occurrence, a tenancy's expected rent and its first day.</summary>
public class PropertyRoutineMathTests
{
    private static readonly LocalDate September = new(2025, 9, 30);

    private static RecurringCost Cost(int count, DurationUnit unit, LocalDate starts, LocalDate? ends = null)
        => new("property_tax", 100m, Loans.Ok(Duration.Create(count, unit)), starts, ends, null);

    private static Tenancy Lease(LocalDate starts, LocalDate? ends = null, decimal rent = 900m, decimal charges = 0m)
        => new("T2", starts, ends, rent, charges, null, null);

    private static LocalDate? Day(string? iso) => iso is null ? null : LocalDatePattern.Iso.Parse(iso).Value;

    [Theory]
    [InlineData(2025, 9, "2025-09-10")]
    [InlineData(2025, 10, null)]
    [InlineData(2025, 11, null)]
    [InlineData(2025, 12, "2025-12-10")]
    [InlineData(2025, 2, null)] // before its first occurrence, 10 March
    public void A_quarterly_cost_falls_in_its_months_only(int year, int month, string? due) // Review Focus #2
        => Assert.Equal(Day(due), PropertyMath.DueIn(Cost(3, DurationUnit.Month, new LocalDate(2025, 3, 10)), new LocalDate(year, month, 1)));

    [Fact]
    public void A_yearly_cost_falls_in_its_month_once_a_year()
    {
        var tax = Cost(1, DurationUnit.Year, new LocalDate(2020, 9, 20));
        Assert.Equal(new LocalDate(2025, 9, 20), PropertyMath.DueIn(tax, September));
        Assert.Null(PropertyMath.DueIn(tax, new LocalDate(2025, 10, 15)));
    }

    [Fact]
    public void A_monthly_cost_on_the_31st_falls_on_each_months_last_day()
    {
        var cost = Cost(1, DurationUnit.Month, new LocalDate(2025, 1, 31));
        Assert.Equal(new LocalDate(2025, 2, 28), PropertyMath.DueIn(cost, new LocalDate(2025, 2, 1)));
        Assert.Equal(new LocalDate(2025, 9, 30), PropertyMath.DueIn(cost, new LocalDate(2025, 9, 1)));
    }

    [Fact]
    public void A_cost_falls_in_no_month_before_its_start_nor_from_its_end()
    {
        var october = Cost(1, DurationUnit.Month, new LocalDate(2025, 10, 1));
        Assert.Null(PropertyMath.DueIn(october, new LocalDate(2025, 9, 1)));
        Assert.Equal(new LocalDate(2025, 10, 1), PropertyMath.DueIn(october, new LocalDate(2025, 10, 31)));
        // Its range is [StartsOn, EndsOn): an end on the due date leaves the month without it, a day later keeps it.
        Assert.Null(PropertyMath.DueIn(Cost(1, DurationUnit.Month, new LocalDate(2021, 1, 5), new LocalDate(2025, 9, 5)), September));
        Assert.Equal(new LocalDate(2025, 9, 5),
            PropertyMath.DueIn(Cost(1, DurationUnit.Month, new LocalDate(2021, 1, 5), new LocalDate(2025, 9, 6)), September));
    }

    [Fact]
    public void A_tenancy_covering_the_whole_month_expects_rent_and_charges_from_the_first()
        => Assert.Equal<(decimal, LocalDate)?>((950m, new LocalDate(2025, 9, 1)),
            PropertyMath.RentIn(Lease(new LocalDate(2021, 1, 1), rent: 900m, charges: 50m), September));

    [Fact]
    public void A_tenancy_starting_mid_month_is_prorated_and_dated_on_its_start() // Review Focus #1
        // 21 days of 30 at 650: 455.00, from 10 September.
        => Assert.Equal<(decimal, LocalDate)?>((455m, new LocalDate(2025, 9, 10)),
            PropertyMath.RentIn(Lease(new LocalDate(2025, 9, 10), rent: 600m, charges: 50m), September));

    [Fact]
    public void A_tenancy_ending_mid_month_is_prorated_and_dated_on_the_first() // Review Focus #1
        // Its end is exclusive: 1 to 9 September, 9 days of 30 at 900: 270.00.
        => Assert.Equal<(decimal, LocalDate)?>((270m, new LocalDate(2025, 9, 1)),
            PropertyMath.RentIn(Lease(new LocalDate(2021, 1, 1), new LocalDate(2025, 9, 10)), September));

    [Fact]
    public void A_tenancy_covering_no_day_of_the_month_expects_nothing() // Review Focus #1
    {
        Assert.Null(PropertyMath.RentIn(Lease(new LocalDate(2021, 1, 1), new LocalDate(2025, 9, 1)), September)); // ended on the 1st
        Assert.Null(PropertyMath.RentIn(Lease(new LocalDate(2025, 10, 1)), September));
    }

    [Fact]
    public void A_partial_month_rounds_half_up_to_the_cent_and_february_counts_its_own_days()
    {
        // 0.15 for one day of 30 is 0.005: half up gives 0.01, never 0.00.
        Assert.Equal<(decimal, LocalDate)?>((0.01m, new LocalDate(2025, 9, 30)),
            PropertyMath.RentIn(Lease(new LocalDate(2025, 9, 30), new LocalDate(2025, 10, 1), rent: 0.15m), September));
        // 15 days of a leap February's 29 at 870: 450.00.
        Assert.Equal<(decimal, LocalDate)?>((450m, new LocalDate(2024, 2, 15)),
            PropertyMath.RentIn(Lease(new LocalDate(2024, 2, 15), rent: 870m), new LocalDate(2024, 2, 1)));
    }
}
