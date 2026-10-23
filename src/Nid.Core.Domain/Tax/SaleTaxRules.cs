using NodaTime;

namespace Nid.Core.Domain.Tax;

/// <summary>
/// Sale tax spec §2, D19: the rates of one revenue year for the tax on a sale, written for <c>RulesYear</c>; a later revenue year
/// carries them forward (<see cref="CarriedForward"/>). The capital social rate follows <see cref="TaxRules"/>
/// (18,6 % from 2026); property gains and life insurance stay at 17,2 %: LFSS 2026, loi n° 2025-1403 du 30 décembre 2025, art. 12
/// (CSS L136-8 IV keeps the 9,2 % CSG for life insurance, property gains and rental income).
/// </summary>
public sealed record SaleTaxRules(
    int RevenueYear, decimal FlatTaxRate, decimal CapitalSocialRate, decimal PropertyIncomeRate, decimal PropertySocialRate,
    decimal LifeInsuranceSocialRate, decimal LifeInsuranceReducedRate, decimal LifeInsuranceAllowanceSingle, decimal LifeInsuranceAllowanceCouple,
    decimal LifeInsurancePremiumThreshold, decimal AcquisitionFlatRate, decimal WorksFlatRate)
{
    public int RulesYear => Math.Min(RevenueYear, 2026);
    public bool CarriedForward => RevenueYear > RulesYear;

    public static SaleTaxRules For(int revenueYear)
    {
        var year = TaxRules.For(revenueYear);
        return new SaleTaxRules(
            revenueYear, year.FlatTaxRate, year.CapitalSocialRate, PropertyIncomeRate: 0.19m, PropertySocialRate: 0.172m,
            LifeInsuranceSocialRate: 0.172m, LifeInsuranceReducedRate: 0.075m, LifeInsuranceAllowanceSingle: 4_600m, LifeInsuranceAllowanceCouple: 9_200m,
            LifeInsurancePremiumThreshold: 150_000m, AcquisitionFlatRate: 0.075m, WorksFlatRate: 0.15m);
    }
}

/// <summary>Allowances for years held on a property gain, as fractions 0..1 (art. 150 VC CGI).</summary>
public static class PropertyAllowance
{
    /// <summary>Income tax: 6 % a year from the 6th to the 21st year, 4 % the 22nd, exempt beyond.</summary>
    public static decimal Income(int yearsHeld) => yearsHeld switch
    {
        <= 5 => 0m,
        <= 21 => (yearsHeld - 5) * 0.06m,
        _ => 1m,
    };

    /// <summary>Social levies: 1,65 % a year from the 6th to the 21st, 1,60 % the 22nd, 9 % a year from the 23rd, exempt after 30 years.</summary>
    public static decimal Social(int yearsHeld) => yearsHeld switch
    {
        <= 5 => 0m,
        <= 21 => (yearsHeld - 5) * 0.0165m,
        22 => 0.28m,
        <= 30 => 0.28m + (yearsHeld - 22) * 0.09m,
        _ => 1m,
    };
}

/// <summary>Surtax on high property gains (art. 1609 nonies G CGI), on the owner's net taxable gain.</summary>
public static class Surtax
{
    // Each row: the gain from which the rate applies, the rate, and the smoothing over the 10 000 € below the next row (null: none).
    private static readonly (decimal From, decimal Rate, decimal? Smoothing)[] Scale =
    [
        (50_000m, 0.02m, 1m / 20m), (60_000m, 0.02m, null),
        (100_000m, 0.03m, 1m / 10m), (110_000m, 0.03m, null),
        (150_000m, 0.04m, 0.15m), (160_000m, 0.04m, null),
        (200_000m, 0.05m, 0.20m), (210_000m, 0.05m, null),
        (250_000m, 0.06m, 0.25m), (260_000m, 0.06m, null),
    ];

    /// <summary>Exact, unrounded.</summary>
    public static decimal Of(decimal netTaxableGain)
    {
        if (netTaxableGain <= Scale[0].From) return 0m;
        for (var i = Scale.Length - 1; i >= 0; i--)
        {
            var (from, rate, smoothing) = Scale[i];
            if (netTaxableGain <= from) continue;
            // the smoothed bracket ends where the next row starts
            return rate * netTaxableGain - (smoothing is { } k ? (Scale[i + 1].From - netTaxableGain) * k : 0m);
        }
        return 0m;
    }
}

/// <summary>Full years held.</summary>
public static class HeldYears
{
    /// <summary>Whole years from <paramref name="from"/> to <paramref name="to"/>: the anniversary counts, a 29 February start has it on 28 February.</summary>
    public static int FullYears(LocalDate from, LocalDate to) => Period.Between(from, to, PeriodUnits.Years).Years;
}
