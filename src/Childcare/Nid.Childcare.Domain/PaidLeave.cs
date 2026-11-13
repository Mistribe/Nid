using Nid.Childcare.Contracts;
using NodaTime;

namespace Nid.Childcare.Domain;

/// <summary>
/// Childcare spec §3 (IDCC 3239 art. 102, DREETS fiches 4 and 6): paid leave of an incomplete year, settled once in June,
/// the higher of the tenth and maintained pay. Pure.
/// </summary>
public static class PaidLeave
{
    /// <summary>The reference period runs from 1 June of the previous year to 31 May.</summary>
    public static LocalDate ReferenceStart(LocalDate june) => new(june.Year - 1, 6, 1);

    /// <summary>
    /// Days acquired over <paramref name="days"/> (the reference period's, or part of it): 2,5 working days per 4 weeks of
    /// effective work, rounded up to the whole day, 30 at most (C. trav. L3141-3 and L3141-4, IDCC 3239 art. 102-1; DREETS
    /// Occitanie fiche 1.5 of 5 November 2024: the month method does not apply to a contract of 46 weeks or fewer, « 2,75
    /// jours doit être arrondi à 3 »). A week counts for its template days worked or assimilated over its template days, so
    /// weeks without care, the days outside the contract and the unpaid absences earn nothing.
    /// </summary>
    public static int Accrued(IEnumerable<StatementDay> days, WeekPlan plan, IReadOnlyList<AbsenceReason>? unpaid = null)
    {
        var reasons = unpaid ?? AbsenceCodes.DefaultUnpaid;
        var weeks = days.GroupBy(d => WeekPlan.MondayOf(d.Date)).Sum(w =>
        {
            var template = plan.Types[plan.TypeOf(w.Key)].Schedule.Count;
            return template == 0 ? 0m : Math.Min(1m, (decimal)w.Count(d => Counts(d, reasons)) / template);
        });
        // Thirds and sevenths of a week are not exact in decimal: a whole result must not round up to the next day.
        return Math.Min(ChildcareRules.LeaveDaysCap, (int)Math.Ceiling(Math.Round(weeks * ChildcareRules.LeaveDaysPerFourWeeks / 4m, 6)));
    }

    /// <summary>
    /// Assimilated to effective work whether paid or not (L3141-5, DREETS fiche 1.5): last year's paid leave, public
    /// holidays off, training on work time. Maternity and work accidents suspend the contract, which Nid does not model.
    /// </summary>
    private static readonly AbsenceReason[] Assimilated = [AbsenceReason.PaidLeave, AbsenceReason.Holiday, AbsenceReason.CarerTraining];

    /// <summary>
    /// A day worked, an assimilated absence, or one the employer pays (the family's convenience: the carer stays available);
    /// an unpaid absence (the child's sickness, the carer's) is not effective work.
    /// shortcut: the carer's own sickness earns nothing, not the 2 days a month of L3141-5 7°; upgrade when absences tell sickness apart.
    /// </summary>
    private static bool Counts(StatementDay day, IReadOnlyList<AbsenceReason> unpaid)
        => day.Present || day.Planned && day.Absence is { } r
            && (Assimilated.Contains(r) || !unpaid.Contains(r) && !AbsenceCodes.AlwaysUnpaid.Contains(r));

    /// <summary>
    /// Tenth of the period's net pay, or maintained pay = days ÷ 6 working days a week × average weekly hours × net
    /// hourly rate (Pajemploi, art. 48.1.1.5; spec §9).
    /// </summary>
    public static decimal JuneIndemnity(decimal referenceNetPay, decimal netHourlyRate, decimal weeklyHours, int days)
        => Math.Max(Mensualisation.Round(referenceNetPay / 10m), Mensualisation.Round(days * weeklyHours * netHourlyRate / 6m));
}
