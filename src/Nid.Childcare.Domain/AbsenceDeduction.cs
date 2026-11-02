using Nid.Childcare.Contracts;

namespace Nid.Childcare.Domain;

/// <summary>
/// Childcare spec §3 (IDCC 3239 art. 111, DREETS fiches 8 and 11): base pay × hours of the deducted days ÷ hours the
/// template plans over the whole month, gap weeks and holidays included. Pure.
/// </summary>
public static class AbsenceDeduction
{
    public static (decimal Deduction, int DeductedDays, int ChildSickDeducted) Compute(
        decimal basePay, IReadOnlyList<StatementDay> days, IReadOnlyList<WeekdaySlot> schedule, int childSickUsedBefore)
    {
        decimal TemplateHours(StatementDay d) => schedule.FirstOrDefault(s => s.Day == d.Date.DayOfWeek) is { } s ? Mensualisation.Hours(s.Arrival, s.Departure) : 0m;

        var sickLeft = Math.Max(0, ChildcareRules.ChildSickQuotaDays - childSickUsedBefore);
        var sick = days.Where(d => d.Absence == AbsenceReason.ChildSick).OrderBy(d => d.Date).Take(sickLeft).ToList();
        var deducted = days.Where(d => d.Absence == AbsenceReason.CarerAbsent).Concat(sick).ToList();
        if (deducted.Count == 0) return (0m, 0, 0);

        var planned = days.Sum(TemplateHours);
        return (Mensualisation.Round(basePay * deducted.Sum(TemplateHours) / planned), deducted.Count, sick.Count);
    }
}
