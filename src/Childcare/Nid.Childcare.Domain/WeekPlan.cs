using Nid.Childcare.Contracts;
using NodaTime;

namespace Nid.Childcare.Domain;

/// <summary>Which template week applies on a day: the types rotate in order from the anchor Monday, overrides win. Pure.</summary>
public sealed record WeekPlan(IReadOnlyList<WeekType> Types, LocalDate Anchor, IReadOnlyList<WeekOverride> Overrides)
{
    public static WeekPlan For(IReadOnlyList<WeekType> types, LocalDate contractStart, IReadOnlyList<WeekOverride> overrides)
        => new(types, MondayOf(contractStart), overrides);

    public static LocalDate MondayOf(LocalDate day) => day.PlusDays(-((int)day.DayOfWeek - 1));

    public int ProgrammedWeeks => Types.Sum(t => t.Weeks);

    /// <summary>The week type the rotation gives the week of this Monday, overrides ignored.</summary>
    public int RotationOf(LocalDate monday)
    {
        var weeks = Period.Between(Anchor, monday, PeriodUnits.Weeks).Weeks;
        return ((weeks % Types.Count) + Types.Count) % Types.Count;
    }

    public int TypeOf(LocalDate day)
    {
        var monday = MondayOf(day);
        return Overrides.FirstOrDefault(o => o.Monday == monday)?.WeekType ?? RotationOf(monday);
    }

    public WeekdaySlot? SlotOn(LocalDate day) => Types[TypeOf(day)].Schedule.FirstOrDefault(s => s.Day == day.DayOfWeek);

    public decimal HoursOn(LocalDate day) => SlotOn(day) is { } s ? Mensualisation.Hours(s.Arrival, s.Departure) : 0m;
}
