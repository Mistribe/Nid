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

    /// <summary>A paid absence is not made up: the hour worked past Tuesday's plan that week is still extra.</summary>
    [Fact]
    public void A_paid_absence_does_not_offset_extra_hours()
    {
        var month = new YearMonth(2026, 11);
        var days = AbsenceDeductionTests.Absent(Around(month).Where(d => d.Date.Month == 11).ToList(), (2, AbsenceReason.ChildSick))
            .Select(d => d.Date.Day == 3 ? d with { Departure = d.Departure!.Value.PlusHours(1) } : d).ToList();

        Assert.Equal((1m, 0m), ExtraHours.Compute(month, days, Plan, Terms with { UnpaidAbsences = [] }));
    }

    /// <summary>A day not planned is worked on top of the week, even when a planned day that week ends early for the family.</summary>
    [Fact]
    public void An_early_departure_does_not_offset_an_unplanned_day()
    {
        var month = new YearMonth(2026, 11);
        var days = With(Around(month), ExtraWednesday).Select(d => d.Date == D(2026, 11, 10) ? d with { Departure = d.Departure!.Value.PlusHours(-2), Absence = AbsenceReason.Family } : d).ToList();

        Assert.Equal((7m, 2m), ExtraHours.Compute(month, days, Plan, Terms with { UnpaidAbsences = [] }));
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

    /// <summary>A training day is always deducted, so it is no longer planned: the Wednesday worked instead stays under 45 h.</summary>
    [Fact]
    public void A_training_day_is_not_planned_for_the_45_hours()
    {
        var month = new YearMonth(2026, 11);
        var training = new StatementDay(D(2026, 11, 12), true, false, null, null, false, AbsenceReason.CarerTraining);
        Assert.Equal((9m, 0m), ExtraHours.Compute(month, With(With(Around(month), ExtraWednesday), training), Plan, Terms with { UnpaidAbsences = [] }));
        Assert.Equal((9m, 0m), ExtraHours.Compute(month, With(With(Around(month), ExtraWednesday), training with { Absence = AbsenceReason.FamilyAgreed }), Plan, Terms with { UnpaidAbsences = [] }));
    }

    /// <summary>Twenty minutes extra in two weeks: 40 minutes in the month, declared as one hour; half an hour goes up, a quarter down.</summary>
    [Fact]
    public void Extra_hours_are_rounded_over_the_month()
    {
        var month = new YearMonth(2026, 11);
        var days = Around(month).Select(d => d.Date == D(2026, 11, 3) || d.Date == D(2026, 11, 10) ? d with { Departure = d.Departure!.Value.PlusMinutes(20) } : d).ToList();
        var (complementary, _) = ExtraHours.Compute(month, days, Plan);
        Assert.Equal(1m, ExtraHours.Whole(complementary));
        Assert.Equal(3m, ExtraHours.Whole(2.5m));
        Assert.Equal(19m, ExtraHours.Whole(19.25m));
        Assert.Equal(0m, ExtraHours.Whole(0m));
    }

    [Fact]
    public void Short_week_is_not_negative()
    {
        var month = new YearMonth(2026, 11);
        var absent = new StatementDay(D(2026, 11, 12), true, false, null, null, false, AbsenceReason.Family);
        Assert.Equal((0m, 0m), ExtraHours.Compute(month, With(Around(month), absent), Plan));
    }

    /// <summary>An extra day counts in its own month, whichever month the week's Thursday is in.</summary>
    [Fact]
    public void Week_spanning_months_splits_by_day()
    {
        var extra = ExtraWednesday with { Date = D(2026, 12, 2) };
        var nov = new YearMonth(2026, 11);
        var dec = new YearMonth(2026, 12);
        Assert.Equal((0m, 0m), ExtraHours.Compute(nov, With(Around(nov), extra), Plan));
        Assert.Equal((7m, 2m), ExtraHours.Compute(dec, With(Around(dec), extra), Plan));
    }

    /// <summary>Monday 2026-11-30 is in a week whose Thursday is in December: its extra hours are November's.</summary>
    [Fact]
    public void A_last_day_of_the_month_keeps_its_extra_hours()
    {
        var monday = D(2026, 11, 30);
        var nov = new YearMonth(2026, 11);
        var dec = new YearMonth(2026, 12);
        var late = Around(nov).Select(d => d.Date == monday ? d with { Departure = d.Departure!.Value.PlusHours(1) } : d).ToList();
        Assert.Equal((1m, 0m), ExtraHours.Compute(nov, late, Plan));
        Assert.Equal((0m, 0m), ExtraHours.Compute(dec, [.. late.Where(d => d.Date >= D(2026, 11, 30)), .. StatementPreparation.Prepare(dec, Plan, Gaps)], Plan));
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

public class ExemptionTests
{
    /// <summary>About Pajemploi's own: 11,43 € for 19 complementary hours, 6,13 € for 7 and 3 overtime ones, at 4 € net.</summary>
    [Fact]
    public void The_exemption_is_estimated_from_the_declared_extra_hours()
    {
        Assert.Equal(11.32m, ExtraHours.ExemptionEstimate(19m, 0m, 4.00m, 0.10m));
        Assert.Equal(6.14m, ExtraHours.ExemptionEstimate(7m, 3m, 4.00m, 0.10m));
        Assert.Equal(0m, ExtraHours.ExemptionEstimate(0m, 0m, 4.00m, 0.10m));
    }
}

public class HolidayPremiumTests
{
    private static StatementDay Worked(LocalDate date, bool planned = true)
        => new(date, planned, true, new LocalTime(8, 15), new LocalTime(18, 0), false, null);

    /// <summary>IDCC 3239: an ordinary public holiday worked adds 10 % to its hours, 1 May 100 %; other days add nothing.</summary>
    [Fact]
    public void A_public_holiday_worked_adds_10_percent_and_1_May_100_percent()
    {
        Assert.Equal(4.19m, ExtraHours.HolidayPremium([Worked(D(2026, 5, 25)), Worked(D(2026, 5, 26))], 4.30m));
        Assert.Equal(41.93m, ExtraHours.HolidayPremium([Worked(D(2026, 5, 1))], 4.30m));
        Assert.Equal(0m, ExtraHours.HolidayPremium([new StatementDay(D(2026, 5, 25), true, false, null, null, false, AbsenceReason.Holiday)], 4.30m));
    }

    /// <summary>The premium goes to the month's extra pay, on top of the extra hours the day makes.</summary>
    [Fact]
    public void The_month_pays_the_premium()
    {
        var month = new YearMonth(2026, 11);
        MonthFigures With(LocalDate wednesday) => ChildcareMonth.Compute(new MonthInput(month, Terms, Plan, Gaps,
            [.. StatementPreparation.Prepare(month, Plan, Gaps).Select(d => d.Date == wednesday ? Worked(d.Date, planned: false) : d)],
            [.. StatementPreparation.Prepare(month.PlusMonths(-1), Plan, Gaps), .. StatementPreparation.Prepare(month.PlusMonths(1), Plan, Gaps)], 0, 0m, 0m, null));

        Assert.Equal(4.19m, With(D(2026, 11, 11)).ExtraPay - With(D(2026, 11, 18)).ExtraPay);
    }
}
