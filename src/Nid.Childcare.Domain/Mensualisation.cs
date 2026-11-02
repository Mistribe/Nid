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

    public static decimal BasePay(decimal netHourlyRate, decimal weeklyHours, int programmedWeeks)
        => Round(netHourlyRate * weeklyHours * programmedWeeks / 12m);

    public static int NormalHours(decimal weeklyHours, int programmedWeeks)
        => (int)Math.Round(weeklyHours * programmedWeeks / 12m, MidpointRounding.AwayFromZero);
}
