using Nid.Childcare.Contracts;

namespace Nid.Childcare.Domain;

/// <summary>
/// Childcare spec §3 (IDCC 3239 art. 111, 46 weeks or fewer, the only contracts Nid takes): base pay × deducted days ÷ days
/// the template plans over the whole month, gap weeks and holidays included. Pure.
/// </summary>
public static class AbsenceDeduction
{
    public static (decimal Deduction, int DeductedDays, int ChildSickDeducted) Compute(
        decimal basePay, IReadOnlyList<StatementDay> days, WeekPlan plan, int childSickUsedBefore, IReadOnlyList<AbsenceReason>? unpaid = null)
    {
        var sickLeft = Math.Max(0, ChildcareRules.ChildSickQuotaDays - childSickUsedBefore);
        var reasons = unpaid ?? AbsenceCodes.DefaultUnpaid;
        // The sick-child quota only limits a reason the contract does not pay.
        var sick = reasons.Contains(AbsenceReason.ChildSick) ? days.Where(d => d.Absence == AbsenceReason.ChildSick && MissingShare(d, plan) > 0m).OrderBy(d => d.Date).Take(sickLeft).ToList() : [];
        var deducted = days.Where(d => d.Absence is { } r && r != AbsenceReason.ChildSick && reasons.Contains(r) && MissingShare(d, plan) > 0m).Concat(sick).ToList();
        if (deducted.Count == 0) return (0m, 0, 0);

        var planned = days.Count(d => plan.HoursOn(d.Date) > 0m);
        // A shortened day counts for the part of it not worked; a sick-child one still takes a whole day of the quota.
        return (Mensualisation.Round(basePay * deducted.Sum(d => MissingShare(d, plan)) / planned), deducted.Count(d => !d.Present), sick.Count);
    }

    /// <summary>The share of the planned day not worked: all of it for an absent day, the missing hours of a shortened one.</summary>
    private static decimal MissingShare(StatementDay day, WeekPlan plan)
    {
        if (!day.Present) return 1m;
        var hours = plan.HoursOn(day.Date);
        return hours > 0m ? Math.Max(0m, hours - Mensualisation.Hours(day.Arrival!.Value, day.Departure!.Value)) / hours : 0m;
    }
}
