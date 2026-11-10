using Nid.Childcare.Contracts;
using Nid.Childcare.Domain;
using NodaTime;
using static Nid.Childcare.Tests.Fixture;

namespace Nid.Childcare.Tests;

public class ExtraHoursTests
{
    private static readonly StatementDay ExtraWednesday = new(D(2026, 11, 11), false, true, new LocalTime(8, 0), new LocalTime(17, 0), false, null);

    /// <summary>Friday 2027-10-01 starts the contract: its week's Thursday has no statement, so the week is October's.</summary>
    [Fact]
    public void The_first_week_of_a_contract_counts_when_its_thursday_comes_before()
    {
        var month = new YearMonth(2027, 10);
        var terms = Terms with { Start = D(2027, 10, 1) };
        var days = StatementPreparation.Prepare(month, Plan, [], terms)
            .Select(d => d.Date.Day == 1 ? d with { Departure = d.Departure!.Value.PlusHours(1) } : d).ToList();
        IReadOnlyList<StatementDay> all = [.. StatementPreparation.Prepare(month.PlusMonths(-1), Plan, [], terms), .. days];

        Assert.Equal((1m, 0m), ExtraHours.Compute(month, all, Plan, terms));
    }

    /// <summary>An hour missing for an unpaid reason, made up on Tuesday: that hour is no longer planned, so it is extra.</summary>
    [Fact]
    public void The_missing_hours_of_an_unpaid_reason_are_no_longer_planned()
    {
        var month = new YearMonth(2026, 11);
        var days = Around(month).Where(d => d.Date.Month == 11).Select(d => d.Date.Day switch
        {
            2 => d with { Departure = d.Departure!.Value.PlusHours(-1), Absence = AbsenceReason.CarerAbsent },
            3 => d with { Departure = d.Departure!.Value.PlusHours(1) },
            _ => d,
        }).ToList();

        Assert.Equal((1m, 0m), ExtraHours.Compute(month, days, Plan));
    }

    /// <summary>A sick-child day the contract pays stays planned: hours made up that week are no extra hours.</summary>
    [Fact]
    public void A_paid_reason_stays_planned()
    {
        var month = new YearMonth(2026, 11);
        var days = AbsenceDeductionTests.Absent(Around(month).Where(d => d.Date.Month == 11).ToList(), (2, AbsenceReason.ChildSick))
            .Select(d => d.Date.Day == 3 ? d with { Departure = d.Departure!.Value.PlusHours(1) } : d).ToList();

        Assert.Equal((0m, 0m), ExtraHours.Compute(month, days, Plan, Terms with { UnpaidAbsences = [] }));
    }

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

    /// <summary>Half an hour extra in two weeks: one started hour in the month, not one per week.</summary>
    [Fact]
    public void Started_hour_is_rounded_over_the_month()
    {
        var month = new YearMonth(2026, 11);
        var days = Around(month).Select(d => d.Date == D(2026, 11, 3) || d.Date == D(2026, 11, 10) ? d with { Departure = d.Departure!.Value.PlusMinutes(20) } : d).ToList();
        var (complementary, _) = ExtraHours.Compute(month, days, Plan);
        Assert.Equal(1m, ExtraHours.Started(complementary));
        Assert.Equal(3m, ExtraHours.Started(2.5m));
        Assert.Equal(0m, ExtraHours.Started(0m));
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
            2 => d with { Present = false, Arrival = null, Departure = null, Lunch = false, Absence = AbsenceReason.CarerAbsent },
            3 => d with { Departure = new LocalTime(19, 30) },
            _ => d,
        }).ToList();
        IReadOnlyList<StatementDay> all = [.. StatementPreparation.Prepare(month.PlusMonths(-1), Plan, Gaps), .. days];
        Assert.Equal((2m, 0m), ExtraHours.Compute(month, all, Plan));
    }
}
