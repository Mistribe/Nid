using Nid.Childcare.Contracts;
using NodaTime;

namespace Nid.Childcare.Domain;

/// <summary>
/// Childcare spec §3: hours worked beyond a week's planned hours, complementary up to 45 h, overtime beyond. A week
/// belongs to the month of its Thursday. Pure.
/// </summary>
public static class ExtraHours
{
    /// <summary><paramref name="days"/> covers every ISO week touching <paramref name="month"/>.</summary>
    public static (decimal Complementary, decimal Overtime) Compute(YearMonth month, IReadOnlyList<StatementDay> days, WeekPlan plan)
    {
        decimal complementary = 0m, overtime = 0m;
        foreach (var week in days.GroupBy(d => d.Date.PlusDays(1 - (int)d.Date.DayOfWeek)))
        {
            if (YearMonth(week.Key.PlusDays(3)) != month) continue;
            // The week's own plan, not a template's fixed hours: a paid absence stays planned, a deducted one and a gap week do not.
            var planned = week.Where(d => d.Planned && d.Absence is not (AbsenceReason.CarerAbsent or AbsenceReason.ChildSick)).Sum(d => plan.HoursOn(d.Date));
            var actual = week.Where(d => d.Present).Sum(d => Mensualisation.Hours(d.Arrival!.Value, d.Departure!.Value));
            var extra = Math.Max(0m, actual - planned);
            var under = Math.Min(extra, Math.Max(0m, ChildcareRules.OvertimeThresholdHours - planned));
            complementary += under;
            overtime += extra - under;
        }
        return (complementary, overtime);
    }

    public static decimal Pay(decimal complementary, decimal overtime, decimal rate, decimal overtimeRate)
        => Mensualisation.Round(complementary * rate + overtime * rate * (1m + overtimeRate));

    private static YearMonth YearMonth(LocalDate d) => new(d.Year, d.Month);
}
