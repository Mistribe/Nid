using NodaTime;

namespace Nid.Childcare.Domain;

/// <summary>
/// Childcare spec §3 (IDCC 3239 art. 102, DREETS fiches 4 and 6): paid leave of an incomplete year, settled once in June,
/// the higher of the tenth and maintained pay; of a full year, paid as maintained pay within the monthly pay, June adding
/// the tenth's excess (DREETS Occitanie, fiche « Accueil sur 52 semaines », January 2023, §5.3). Pure.
/// </summary>
public static class PaidLeave
{
    /// <summary>The reference period runs from 1 June of the previous year to 31 May.</summary>
    public static LocalDate ReferenceStart(LocalDate june) => new(june.Year - 1, 6, 1);

    /// <summary>2,5 working days per month with care, rounded up to the whole day, 30 at most.</summary>
    public static int Accrued(decimal monthsWithCare)
        => Math.Min(ChildcareRules.LeaveDaysCap, (int)Math.Ceiling(ChildcareRules.LeaveDaysPerMonth * monthsWithCare));

    /// <summary>
    /// Tenth of the period's net pay, or maintained pay = days ÷ 6 working days a week × average weekly hours × net
    /// hourly rate (Pajemploi, art. 48.1.1.5; spec §9).
    /// </summary>
    public static decimal JuneIndemnity(decimal referenceNetPay, decimal netHourlyRate, decimal weeklyHours, int days)
        => Math.Max(Tenth(referenceNetPay), Maintained(netHourlyRate, weeklyHours, days));

    /// <summary>
    /// A full year: the maintained pay is already in the monthly pay, so June pays only what the tenth of the reference period
    /// (leave pay included, Code du travail L3141-24) adds over it for the days accrued, never less than nothing.
    /// </summary>
    public static decimal JuneDifference(decimal referenceNetPay, decimal netHourlyRate, decimal weeklyHours, int days)
        => Math.Max(0m, Tenth(referenceNetPay) - Maintained(netHourlyRate, weeklyHours, days));

    private static decimal Tenth(decimal referenceNetPay) => Mensualisation.Round(referenceNetPay / 10m);

    private static decimal Maintained(decimal netHourlyRate, decimal weeklyHours, int days) => Mensualisation.Round(days * weeklyHours * netHourlyRate / 6m);
}
