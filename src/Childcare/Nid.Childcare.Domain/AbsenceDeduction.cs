using Nid.Childcare.Contracts;

namespace Nid.Childcare.Domain;

/// <summary>
/// Childcare spec §3 (46 weeks or fewer, or 52 with the leave inside; IDCC 3239 art. 111 and DREETS fiches): the monthly pay × hours not worked ÷ hours the
/// template plans over the whole month, gap weeks and holidays included (Cour de cassation, 11 February 1982). Pure.
/// </summary>
public static class AbsenceDeduction
{
    /// <param name="leaveLeft">
    /// A full year only: the leave days still accrued before this month. Leave days are paid up to it and deducted beyond, as
    /// leave without pay (DREETS Occitanie, fiche « Accueil sur 52 semaines »: 5 weeks « rémunérés ou pris sans solde en fonction
    /// des droits acquis »); the contract's unpaid reasons do not apply to them. Null: leave days follow the unpaid reasons.
    /// </param>
    public static (decimal Deduction, int DeductedDays, int ChildSickDeducted, int PaidLeaveTaken) Compute(
        decimal monthlyPay, IReadOnlyList<StatementDay> days, WeekPlan plan, int childSickUsedBefore, IReadOnlyList<AbsenceReason>? unpaid = null,
        decimal? potentialHours = null, int? leaveLeft = null)
    {
        var (hours, deductedDays, childSick, leaveTaken) = Deducted(days, plan, childSickUsedBefore, unpaid, leaveLeft);
        if (hours == 0m) return (0m, 0, 0, leaveTaken);

        var potential = potentialHours ?? days.Sum(d => plan.HoursOn(d.Date));
        return (Mensualisation.Round(monthlyPay * hours / potential), deductedDays, childSick, leaveTaken);
    }

    /// <summary>The planned hours not worked that the pay does not cover, the absent days among them, the sick-child days they take from the quota
    /// and a full year's leave days paid within the monthly pay.</summary>
    public static (decimal Hours, int DeductedDays, int ChildSickDeducted, int PaidLeaveTaken) Deducted(
        IReadOnlyList<StatementDay> days, WeekPlan plan, int childSickUsedBefore, IReadOnlyList<AbsenceReason>? unpaid = null, int? leaveLeft = null)
    {
        var sickLeft = Math.Max(0, ChildcareRules.ChildSickQuotaDays - childSickUsedBefore);
        var reasons = unpaid ?? AbsenceCodes.DefaultUnpaid;
        // The sick-child quota only limits a reason the contract does not pay.
        var sick = reasons.Contains(AbsenceReason.ChildSick) ? days.Where(d => d.Absence == AbsenceReason.ChildSick && MissingShare(d, plan) > 0m).OrderBy(d => d.Date).Take(sickLeft).ToList() : [];
        var leave = leaveLeft is null ? [] : days.Where(d => d.Absence == AbsenceReason.PaidLeave && MissingShare(d, plan) > 0m).OrderBy(d => d.Date).ToList();
        var paidLeave = Math.Min(leave.Count, Math.Max(0, leaveLeft ?? 0));
        var deducted = days.Where(d => d.Absence is { } r && r != AbsenceReason.ChildSick && !(leaveLeft is not null && r == AbsenceReason.PaidLeave)
                && (reasons.Contains(r) || AbsenceCodes.AlwaysUnpaid.Contains(r)) && MissingShare(d, plan) > 0m)
            .Concat(sick).Concat(leave.Skip(paidLeave)).ToList();
        // A shortened day counts for its hours not worked; a sick-child one still takes a whole day of the quota.
        return (deducted.Sum(d => MissingShare(d, plan)), deducted.Count(d => !d.Present), sick.Count, paidLeave);
    }

    /// <summary>The planned hours not worked: all of them for an absent day, the missing ones of a shortened day, never below zero.</summary>
    private static decimal MissingShare(StatementDay day, WeekPlan plan)
    {
        var hours = plan.HoursOn(day.Date);
        return day.Present ? Math.Max(0m, hours - Mensualisation.Hours(day.Arrival!.Value, day.Departure!.Value)) : hours;
    }
}
