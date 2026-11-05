using Nid.Childcare.Contracts;
using NodaTime;

namespace Nid.Childcare.Domain;

/// <summary>Childcare spec §5: a month's statement as the template plans it, before any correction. Pure.</summary>
public static class StatementPreparation
{
    public static IReadOnlyList<StatementDay> Prepare(YearMonth month, WeekPlan plan, IReadOnlyList<CareGap> gaps, ContractTerms? terms = null)
    {
        var holidays = FrenchHolidays.In(month.Year);
        var days = new List<StatementDay>();
        for (var day = month.OnDayOfMonth(1); day <= month.OnDayOfMonth(month.Calendar.GetDaysInMonth(month.Year, month.Month)); day = day.PlusDays(1))
        {
            var slot = plan.SlotOn(day);
            if (slot is null || !IsPlanned(day, plan, gaps, terms))
                days.Add(new(day, false, false, null, null, false, null));
            else if (holidays.Contains(day))
                days.Add(new(day, true, false, null, null, false, AbsenceReason.Holiday));
            else
                days.Add(new(day, true, true, slot.Arrival, slot.Departure, slot.Meal, null));
        }
        return days;
    }

    /// <summary>In its week type, outside the gaps and, given a contract, within its dates.</summary>
    public static bool IsPlanned(LocalDate day, WeekPlan plan, IReadOnlyList<CareGap> gaps, ContractTerms? terms)
        => plan.SlotOn(day) is not null && !InGap(day, gaps) && (terms is null || InContract(day, terms));

    public static bool InContract(LocalDate day, ContractTerms terms) => day >= terms.Start && (terms.End is not { } end || day <= end);

    public static bool InGap(LocalDate day, IReadOnlyList<CareGap> gaps)
        => gaps.Any(g => day >= g.Monday && day < g.Monday.PlusWeeks(g.Weeks));
}
