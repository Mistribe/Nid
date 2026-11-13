using Nid.Childcare.Contracts;

namespace Nid.Childcare.Domain;

/// <summary>
/// Childcare spec §3 (46 weeks or fewer, the only contracts Nid takes): the monthly pay × hours not worked ÷ hours the
/// template plans over the whole month, gap weeks and holidays included (Cour de cassation, 11 February 1982). Pure.
/// </summary>
public static class AbsenceDeduction
{
    public static (decimal Deduction, int DeductedDays, int ChildSickDeducted) Compute(
        decimal monthlyPay, IReadOnlyList<StatementDay> days, WeekPlan plan, int childSickUsedBefore, IReadOnlyList<AbsenceReason>? unpaid = null,
        decimal? potentialHours = null)
    {
        var sickLeft = Math.Max(0, ChildcareRules.ChildSickQuotaDays - childSickUsedBefore);
        var reasons = unpaid ?? AbsenceCodes.DefaultUnpaid;
        // The sick-child quota only limits a reason the contract does not pay.
        var sick = reasons.Contains(AbsenceReason.ChildSick) ? days.Where(d => d.Absence == AbsenceReason.ChildSick && MissingShare(d, plan) > 0m).OrderBy(d => d.Date).Take(sickLeft).ToList() : [];
        var deducted = days.Where(d => d.Absence is { } r && r != AbsenceReason.ChildSick && (reasons.Contains(r) || AbsenceCodes.AlwaysUnpaid.Contains(r)) && MissingShare(d, plan) > 0m).Concat(sick).ToList();
        if (deducted.Count == 0) return (0m, 0, 0);

        var potential = potentialHours ?? days.Sum(d => plan.HoursOn(d.Date));
        // A shortened day counts for its hours not worked; a sick-child one still takes a whole day of the quota.
        return (Mensualisation.Round(monthlyPay * deducted.Sum(d => MissingShare(d, plan)) / potential), deducted.Count(d => !d.Present), sick.Count);
    }

    /// <summary>The planned hours not worked: all of them for an absent day, the missing ones of a shortened day, never below zero.</summary>
    private static decimal MissingShare(StatementDay day, WeekPlan plan)
    {
        var hours = plan.HoursOn(day.Date);
        return day.Present ? Math.Max(0m, hours - Mensualisation.Hours(day.Arrival!.Value, day.Departure!.Value)) : hours;
    }
}
