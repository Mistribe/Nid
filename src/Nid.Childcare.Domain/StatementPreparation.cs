using Nid.Childcare.Contracts;
using NodaTime;

namespace Nid.Childcare.Domain;

/// <summary>Childcare spec §5: a month's statement as the template plans it, before any correction. Pure.</summary>
public static class StatementPreparation
{
    public static IReadOnlyList<StatementDay> Prepare(YearMonth month, IReadOnlyList<WeekdaySlot> schedule, IReadOnlyList<CareGap> gaps, ContractTerms? terms = null)
    {
        var holidays = FrenchHolidays.In(month.Year);
        var days = new List<StatementDay>();
        for (var day = month.OnDayOfMonth(1); day <= month.OnDayOfMonth(month.Calendar.GetDaysInMonth(month.Year, month.Month)); day = day.PlusDays(1))
        {
            var slot = schedule.FirstOrDefault(s => s.Day == day.DayOfWeek);
            if (slot is null || !IsPlanned(day, schedule, gaps, terms))
                days.Add(new(day, false, false, null, null, false, null));
            else if (holidays.Contains(day))
                days.Add(new(day, true, false, null, null, false, AbsenceReason.Holiday));
            else
                days.Add(new(day, true, true, slot.Arrival, slot.Departure, slot.Meal, null));
        }
        return days;
    }

    /// <summary>In the template, outside the gaps and, given a contract, within its dates.</summary>
    public static bool IsPlanned(LocalDate day, IReadOnlyList<WeekdaySlot> schedule, IReadOnlyList<CareGap> gaps, ContractTerms? terms)
        => schedule.Any(s => s.Day == day.DayOfWeek) && !InGap(day, gaps) && (terms is null || InContract(day, terms));

    public static bool InContract(LocalDate day, ContractTerms terms) => day >= terms.Start && (terms.End is not { } end || day <= end);

    public static bool InGap(LocalDate day, IReadOnlyList<CareGap> gaps)
        => gaps.Any(g => day >= g.Monday && day < g.Monday.PlusWeeks(g.Weeks));
}
