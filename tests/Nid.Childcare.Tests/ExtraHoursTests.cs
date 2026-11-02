using Nid.Childcare.Contracts;
using Nid.Childcare.Domain;
using NodaTime;
using static Nid.Childcare.Tests.Fixture;

namespace Nid.Childcare.Tests;

public class ExtraHoursTests
{
    private static readonly StatementDay ExtraWednesday = new(D(2026, 11, 11), false, true, new LocalTime(8, 0), new LocalTime(17, 0), false, null);

    private static IReadOnlyList<StatementDay> Around(YearMonth month)
        => [.. StatementPreparation.Prepare(month.PlusMonths(-1), Plan, Gaps), .. StatementPreparation.Prepare(month, Plan, Gaps),
            .. StatementPreparation.Prepare(month.PlusMonths(1), Plan, Gaps)];

    private static IReadOnlyList<StatementDay> With(IReadOnlyList<StatementDay> days, StatementDay replacement)
        => [.. days.Select(d => d.Date == replacement.Date ? replacement : d)];

    [Fact]
    public void Week_of_47_hours()
    {
        var month = new YearMonth(2026, 11);
        Assert.Equal((7m, 2m), ExtraHours.Compute(month, With(Around(month), ExtraWednesday), Plan));
        Assert.Equal(36.80m, ExtraHours.Pay(7m, 2m, 4m, 0.10m));
    }

    [Fact]
    public void Short_week_is_not_negative()
    {
        var month = new YearMonth(2026, 11);
        var absent = new StatementDay(D(2026, 11, 12), true, false, null, null, false, AbsenceReason.Family);
        Assert.Equal((0m, 0m), ExtraHours.Compute(month, With(Around(month), absent), Plan));
    }

    [Fact]
    public void Week_spanning_months_goes_to_its_thursday()
    {
        var extra = ExtraWednesday with { Date = D(2026, 12, 2) };
        var nov = new YearMonth(2026, 11);
        var dec = new YearMonth(2026, 12);
        Assert.Equal((0m, 0m), ExtraHours.Compute(nov, With(Around(nov), extra), Plan));
        Assert.Equal((7m, 2m), ExtraHours.Compute(dec, With(Around(dec), extra), Plan));
    }
}

public class ExtraHoursWeekTypeTests
{
    [Fact]
    public void A_short_week_counts_extra_hours_against_its_own_plan()
    {
        var month = new YearMonth(2026, 11);
        var plan = Household(D(2026, 9, 1));
        // Week of 2 November is short: 37.75 h planned; the Tuesday runs to 18:00 instead of 16:45, so 39 h worked.
        var days = StatementPreparation.Prepare(month, plan, []).Select(d => d.Date == D(2026, 11, 3) ? d with { Departure = new LocalTime(18, 0) } : d).ToList();
        Assert.Equal((1.25m, 0m), ExtraHours.Compute(month, days, plan));
    }
}

public class ExtraHoursAbsenceTests
{
    [Fact]
    public void Deducted_absence_does_not_hide_extra_hours()
    {
        var month = new YearMonth(2026, 11);
        var days = StatementPreparation.Prepare(month, Plan, Gaps).Select(d => d.Date.Day switch
        {
            2 => d with { Present = false, Arrival = null, Departure = null, Meal = false, Absence = AbsenceReason.CarerAbsent },
            3 => d with { Departure = new LocalTime(19, 30) },
            _ => d,
        }).ToList();
        IReadOnlyList<StatementDay> all = [.. StatementPreparation.Prepare(month.PlusMonths(-1), Plan, Gaps), .. days];
        Assert.Equal((2m, 0m), ExtraHours.Compute(month, all, Plan));
    }
}
