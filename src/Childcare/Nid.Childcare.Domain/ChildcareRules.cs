using NodaTime;

namespace Nid.Childcare.Domain;

/// <summary>
/// Childcare spec §4: the regulatory values of a childminder's month, by dated period. The minimum guaranteed (MG) is
/// 4,22 € from 1 November 2024, 4,25 € from 1 January 2026 and 4,35 € from 1 June 2026; the other values come from IDCC 3239 and the DREETS
/// Bourgogne-Franche-Comté guide of June 2026.
/// </summary>
public sealed record ChildcareRules(LocalDate From, decimal MinimumGuaranteed)
{
    // 4,22 € from 1 November 2024: décret n° 2024-951 du 23 octobre 2024.
    private static readonly ChildcareRules[] Periods = [new(new(2024, 11, 1), 4.22m), new(new(2026, 1, 1), 4.25m), new(new(2026, 6, 1), 4.35m)];

    /// <summary>The first day the table covers; months before it cannot be computed.</summary>
    public static LocalDate FirstDay => Periods[0].From;

    /// <summary>The legal upkeep minimum for a 9-hour day: 90 % of the MG, up to the cent so it is never below (3,825 € → 3,83 €), as payroll tools pay it.</summary>
    public decimal Upkeep9hMinimum => Math.Ceiling(0.9m * MinimumGuaranteed * 100m) / 100m;

    /// <summary>The upkeep due for a day, whatever its length (IDCC 3239 art. 114).</summary>
    public const decimal UpkeepDailyFloor = 2.65m;
    public const decimal OvertimeThresholdHours = 45m;
    /// <summary>Sick-child days the employer may deduct per contract year, with a certificate (art. 105).</summary>
    public const int ChildSickQuotaDays = 5;
    public const decimal LeaveDaysPerMonth = 2.5m;
    public const int LeaveDaysCap = 30;
    /// <summary>An incomplete year's most programmed weeks (IDCC 3239 art. 97.1: 46 weeks or fewer over twelve months).</summary>
    public const int MaxProgrammedWeeks = 46;
    /// <summary>A full year's weeks (art. 97: 52 weeks, the carer's 5 weeks of paid leave included).</summary>
    public const int FullYearWeeks = 52;

    /// <summary>The period in force on <paramref name="day"/>; the latest one runs on until a newer one is added.</summary>
    public static ChildcareRules For(LocalDate day)
        => Periods.LastOrDefault(p => p.From <= day)
           ?? throw new ArgumentOutOfRangeException(nameof(day), $"Childcare rules start on {Periods[0].From:uuuu-MM-dd}.");
}
