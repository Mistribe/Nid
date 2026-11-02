using NodaTime;

namespace Nid.Childcare.Domain;

/// <summary>
/// Childcare spec §4: the regulatory values of a childminder's month, by dated period. The minimum guaranteed (MG) is
/// 4,25 € from 1 January 2026 and 4,35 € from 1 June 2026; the other values come from IDCC 3239 and the DREETS
/// Bourgogne-Franche-Comté guide of June 2026.
/// </summary>
public sealed record ChildcareRules(LocalDate From, decimal MinimumGuaranteed)
{
    private static readonly ChildcareRules[] Periods = [new(new(2026, 1, 1), 4.25m), new(new(2026, 6, 1), 4.35m)];

    /// <summary>The legal upkeep minimum for a 9-hour day: 90 % of the MG, unrounded.</summary>
    public decimal Upkeep9hMinimum => 0.9m * MinimumGuaranteed;

    /// <summary>The upkeep due for a day, whatever its length (IDCC 3239 art. 114).</summary>
    public const decimal UpkeepDailyFloor = 2.65m;
    public const decimal OvertimeThresholdHours = 45m;
    /// <summary>Sick-child days the employer may deduct per contract year, with a certificate (art. 105).</summary>
    public const int ChildSickQuotaDays = 5;
    public const decimal LeaveDaysPerMonth = 2.5m;
    public const int LeaveDaysCap = 30;
    /// <summary>Beyond this, the contract is a full year, which Nid does not model.</summary>
    public const int MaxProgrammedWeeks = 46;

    /// <summary>The period in force on <paramref name="day"/>; the latest one runs on until a newer one is added.</summary>
    public static ChildcareRules For(LocalDate day)
        => Periods.LastOrDefault(p => p.From <= day)
           ?? throw new ArgumentOutOfRangeException(nameof(day), $"Childcare rules start on {Periods[0].From:uuuu-MM-dd}.");
}
