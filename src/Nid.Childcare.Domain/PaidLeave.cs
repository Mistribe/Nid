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

    /// <summary>2,5 working days per month with care, rounded up to the whole day, 30 at most.</summary>
    public static int Accrued(int monthsWithCare)
        => Math.Min(ChildcareRules.LeaveDaysCap, (int)Math.Ceiling(ChildcareRules.LeaveDaysPerMonth * monthsWithCare));

    /// <summary>Tenth of the period's net pay, or maintained pay = base pay × days ÷ 26 working days.</summary>
    public static decimal JuneIndemnity(decimal referenceNetPay, decimal monthlyBasePay, int days)
        => Math.Max(Mensualisation.Round(referenceNetPay / 10m), Mensualisation.Round(monthlyBasePay * days / 26m));
}
