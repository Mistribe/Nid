using Nid.Childcare.Contracts;
using NodaTime;

namespace Nid.Childcare.Domain;

/// <summary>Childcare spec §3: the monthly salary smoothed over the programmed weeks of an incomplete year. Pure.</summary>
public static class Mensualisation
{
    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>The hours between two times of one day, unrounded.</summary>
    public static decimal Hours(LocalTime from, LocalTime to) => Period.Between(from, to, PeriodUnits.Minutes).Minutes / 60m;

    public static decimal WeeklyHours(IReadOnlyList<WeekdaySlot> schedule) => schedule.Sum(s => Hours(s.Arrival, s.Departure));

    /// <summary>52 weeks less the gaps; more than 46 is a full year, refused.</summary>
    public static int ProgrammedWeeks(IReadOnlyList<CareGap> gaps)
    {
        if (gaps.FirstOrDefault(g => g.Monday.DayOfWeek != IsoDayOfWeek.Monday || g.Weeks < 1) is { } bad)
            throw new ArgumentException($"A gap starts on a Monday and lasts at least a week: {bad.Monday:uuuu-MM-dd}.", nameof(gaps));
        var weeks = 52 - gaps.Sum(g => g.Weeks);
        if (weeks > ChildcareRules.MaxProgrammedWeeks)
            throw new ArgumentException($"{weeks} programmed weeks is a full year; at most {ChildcareRules.MaxProgrammedWeeks}.", nameof(gaps));
        return weeks;
    }

    public static decimal BasePay(decimal netHourlyRate, decimal weeklyHours, int programmedWeeks)
        => Round(netHourlyRate * weeklyHours * programmedWeeks / 12m);

    public static int NormalHours(decimal weeklyHours, int programmedWeeks)
        => (int)Math.Round(weeklyHours * programmedWeeks / 12m, MidpointRounding.AwayFromZero);
}
