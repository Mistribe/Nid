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

    /// <summary>The year's hours: each week type's weekly hours times its weeks.</summary>
    public static decimal AnnualHours(IReadOnlyList<WeekType> types) => types.Sum(t => WeeklyHours(t.Schedule) * t.Weeks);

    public static decimal BasePay(decimal netHourlyRate, decimal annualHours) => Round(netHourlyRate * annualHours / 12m);

    public static int NormalHours(decimal annualHours) => (int)Math.Round(annualHours / 12m, MidpointRounding.AwayFromZero);

    /// <summary>The normal hours of a reduced month (Urssaf, salaire minoré): the pay due over the contract's hourly rate.</summary>
    public static int NormalHours(decimal payDue, decimal netHourlyRate) => (int)Math.Round(payDue / netHourlyRate, MidpointRounding.AwayFromZero);

    /// <summary>The Pajemploi activity days of a month: the year's days over twelve.</summary>
    public static int ActivityDays(IReadOnlyList<WeekType> types)
        => (int)Math.Ceiling(types.Sum(t => t.Schedule.Count * t.Weeks) / 12m);
}
