using Nid.Childcare.Contracts;
using Nid.Childcare.Domain;
using NodaTime;
using static Nid.Childcare.Tests.Fixture;

namespace Nid.Childcare.Tests;

public class PaidLeaveTests
{
    [Fact]
    public void Reference_period_starts_the_june_before() => Assert.Equal(D(2026, 6, 1), PaidLeave.ReferenceStart(D(2027, 6, 1)));

    /// <summary>The fixture's template days from <paramref name="from"/> to <paramref name="to"/>, holidays off included.</summary>
    private static List<StatementDay> Days(LocalDate from, LocalDate to, IReadOnlyList<CareGap> gaps)
    {
        var days = new List<StatementDay>();
        for (var m = from.ToYearMonth(); m.CompareTo(to.ToYearMonth()) <= 0; m = m.PlusMonths(1))
            days.AddRange(StatementPreparation.Prepare(m, Plan, gaps).Where(d => d.Date >= from && d.Date <= to));
        return days;
    }

    private static List<StatementDay> Absent(List<StatementDay> days, AbsenceReason reason, params LocalDate[] dates)
        => [.. days.Select(d => dates.Contains(d.Date) ? d with { Present = false, Arrival = null, Departure = null, Lunch = false, Absence = reason } : d)];

    /// <summary>1 June 2026 (a Monday) to 31 May 2027 (a Monday): 52 ¼ weeks, 32,66 days, held at 30.</summary>
    [Fact]
    public void Full_reference_period_is_capped() => Assert.Equal(30, PaidLeave.Accrued(Days(D(2026, 6, 1), D(2027, 5, 31), []), Plan));

    /// <summary>Six weeks without care leave 46 ¼ weeks: 28,91 days, so 29.</summary>
    [Fact]
    public void Weeks_without_care_earn_nothing()
        => Assert.Equal(29, PaidLeave.Accrued(Days(D(2026, 6, 1), D(2027, 5, 31), [new(D(2026, 8, 3), 4), new(D(2027, 2, 15), 2)]), Plan));

    /// <summary>
    /// From Tuesday 1 September 2026: ¾ of a week, 38 weeks, the Monday 31 May, less two weeks without care: 37 weeks, 23,13
    /// days, so 24. Counting nine months would give 23; counting the calendar months the contract covers counts weeks without care.
    /// </summary>
    [Fact]
    public void Contract_starting_mid_period() => Assert.Equal(24, PaidLeave.Accrued(Days(D(2026, 9, 1), D(2027, 5, 31), [new(D(2027, 2, 15), 2)]), Plan));

    /// <summary>Four sick-child days, unpaid, take a week off the 37: 36 weeks, 22,5 days, rounded up to 23. Four days of the family's convenience, paid, take nothing.</summary>
    [Fact]
    public void Unpaid_absences_earn_nothing()
    {
        var days = Days(D(2026, 9, 1), D(2027, 5, 31), [new(D(2027, 2, 15), 2)]);
        LocalDate[] week = [D(2026, 11, 9), D(2026, 11, 10), D(2026, 11, 12), D(2026, 11, 13)];
        Assert.Equal(23, PaidLeave.Accrued(Absent(days, AbsenceReason.ChildSick, week), Plan));
        Assert.Equal(24, PaidLeave.Accrued(Absent(days, AbsenceReason.Family, week), Plan));
        // The contract decides: a sick-child day it pays counts.
        Assert.Equal(24, PaidLeave.Accrued(Absent(days, AbsenceReason.ChildSick, week), Plan, [AbsenceReason.CarerAbsent]));
        // Training is assimilated to work, though the employer does not pay it.
        Assert.Equal(24, PaidLeave.Accrued(Absent(days, AbsenceReason.CarerTraining, week), Plan));
    }

    /// <summary>Four weeks give 2,5 days, rounded up to 3; a single day of four gives 0,16, so 1.</summary>
    [Fact]
    public void Rounds_up_to_the_whole_day()
    {
        Assert.Equal(3, PaidLeave.Accrued(Days(D(2026, 9, 7), D(2026, 10, 4), []), Plan));
        Assert.Equal(1, PaidLeave.Accrued(Days(D(2026, 9, 7), D(2026, 9, 7), []), Plan));
        Assert.Equal(0, PaidLeave.Accrued([], Plan));
    }

    [Fact]
    public void Maintained_pay_wins() => Assert.Equal(817.00m, PaidLeave.JuneIndemnity(6500m, 4.30m, 38m, 30));

    [Fact]
    public void Tenth_wins() => Assert.Equal(840.00m, PaidLeave.JuneIndemnity(8400m, 4.30m, 38m, 30));

    [Fact]
    public void Pajemploi_example() => Assert.Equal(600.00m, PaidLeave.JuneIndemnity(5400m, 5m, 40m, 18));

    [Fact]
    public void Average_week_of_an_uneven_year() => Assert.Equal(825.36m, PaidLeave.JuneIndemnity(6500m, 4.30m, 1727.5m / 45m, 30));
}
