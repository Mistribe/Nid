using Nid.Childcare.Contracts;

namespace Nid.Childcare.Domain;

/// <summary>
/// Childcare spec §3 (IDCC 3239 art. 111, DREETS fiches 8 and 11): base pay × hours of the deducted days ÷ hours the
/// template plans over the whole month, gap weeks and holidays included. Pure.
/// </summary>
public static class AbsenceDeduction
{
    public static (decimal Deduction, int DeductedDays, int ChildSickDeducted) Compute(
        decimal basePay, IReadOnlyList<StatementDay> days, WeekPlan plan, int childSickUsedBefore)
    {
        var sickLeft = Math.Max(0, ChildcareRules.ChildSickQuotaDays - childSickUsedBefore);
        var sick = days.Where(d => d.Absence == AbsenceReason.ChildSick).OrderBy(d => d.Date).Take(sickLeft).ToList();
        var deducted = days.Where(d => d.Absence == AbsenceReason.CarerAbsent).Concat(sick).ToList();
        if (deducted.Count == 0) return (0m, 0, 0);

        var planned = days.Sum(d => plan.HoursOn(d.Date));
        return (Mensualisation.Round(basePay * deducted.Sum(d => plan.HoursOn(d.Date)) / planned), deducted.Count, sick.Count);
    }
}
