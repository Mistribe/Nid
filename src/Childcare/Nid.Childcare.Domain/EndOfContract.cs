using Nid.Childcare.Contracts;
using NodaTime;

namespace Nid.Childcare.Domain;

/// <summary>
/// The end of a childminder's contract (convention collective IDCC 3239 of 15 March 2021, socle spécifique assistant maternel,
/// art. 118 to 125): notice, breach indemnity, salary regularisation of an incomplete year. Sources: service-public.gouv.fr F16842
/// (« Rupture du contrat par le particulier employeur ») and F33164 (« Démission »), DREETS Grand Est « Les différents modes de rupture
/// du contrat de travail pour les assistants maternels » (July 2022), Pajemploi « Les indemnités de fin de contrat ». Pure.
/// </summary>
public static class EndOfContract
{
    /// <summary>Seniority for the breach indemnity: 9 months of care (art. 121, F16842).</summary>
    public const int IndemnityMonths = 9;

    /// <summary>Seniority in whole months, on the day the letter is sent or handed over (art. 120, F16842).</summary>
    public static int SeniorityMonths(LocalDate start, LocalDate notified)
        => notified < start ? 0 : Period.Between(start, notified, PeriodUnits.Months).Months;

    /// <summary>
    /// The last day of the notice, the default end of the contract (art. 120): 8 calendar days under 3 months of care, 15 up to a
    /// year, a month beyond, counted from the day the letter is first presented. None for misconduct, a change of the approval or the
    /// trial period: the contract ends on the notification. The child's death ends it that day, but the notice is still paid
    /// (art. 63-3-2): Nid runs the contract to the notice's end, its days absent at the family's charge, so their pay is the notice
    /// indemnity. Service-public (F33164) gives no notice to a carer resigning under 3 months; the convention's 8 days stand here.
    /// </summary>
    public static LocalDate NoticeEnd(EndReason reason, LocalDate start, LocalDate notified)
    {
        if (reason is EndReason.Misconduct or EndReason.Approval or EndReason.Trial)
            return notified;
        var months = SeniorityMonths(start, notified);
        return months < 3 ? notified.PlusDays(7) : months < 12 ? notified.PlusDays(14) : notified.PlusMonths(1).PlusDays(-1);
    }

    /// <summary>
    /// Art. 121, F16842: the employer withdrawing the child owes the indemnity from 9 months of care, the child's death whatever the
    /// seniority; never for serious or gross misconduct, a change, suspension or withdrawal of the approval, or a resignation.
    /// </summary>
    public static bool IndemnityDue(EndReason reason, LocalDate start, LocalDate notified)
        => reason == EndReason.ChildDeath || reason == EndReason.Withdrawal && SeniorityMonths(start, notified) >= IndemnityMonths;

    /// <summary>
    /// 1/80 of the gross salaries of the whole contract, upkeep, meals and mileage left out (art. 121). Nid keeps net pay: the gross is
    /// the net times the contract's gross-to-net hourly ratio.
    /// </summary>
    public static decimal RuptureIndemnity(decimal netSalaries, decimal grossRatio) => Mensualisation.Round(netSalaries * grossRatio / 80m);

    /// <summary>
    /// The hours a month owes at the hourly rate, for the regularisation of an incomplete year (art. 124): its planned hours, less
    /// those deducted. A paid absence (a public holiday, the family's convenience) stays owed; extra hours are paid apart. The
    /// regularisation is these hours at their rate less the mensualised pay after deductions, over the whole contract, « au crédit et
    /// au débit de l'assistant maternel » (DREETS, quoting art. 124): below 0 when the carer was paid for hours the child was not
    /// welcomed. Service-public (F16842) reads it as only in the carer's favour; Nid shows the convention's balance either way.
    /// </summary>
    public static decimal HoursDue(IReadOnlyList<StatementDay> days, WeekPlan plan, int childSickUsedBefore, IReadOnlyList<AbsenceReason>? unpaid)
        => days.Where(d => d.Planned).Sum(d => plan.HoursOn(d.Date)) - AbsenceDeduction.Deducted(days, plan, childSickUsedBefore, unpaid).Hours;
}
