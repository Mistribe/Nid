namespace Nid.Core.Domain.Tax;

/// <summary>One bracket of the progressive scale, per share: <c>Rate</c> applies from <c>From</c> up to the next bracket.</summary>
public sealed record TaxBracket(decimal From, decimal Rate);

/// <summary>
/// Tax estimate spec §3: the parameters of one revenue year. <c>RulesYear</c> is the year they were written for: a later revenue
/// year carries the last known scale forward (<see cref="CarriedForward"/>), only the capital social levies follow the LFSS.
/// </summary>
public sealed record TaxRules(
    int RevenueYear, int RulesYear, IReadOnlyList<TaxBracket> Brackets,
    decimal SalaryAllowanceRate, decimal SalaryAllowanceMin, decimal SalaryAllowanceMax,
    decimal FamilyCapPerHalfShare, decimal DecoteSingleThreshold, decimal DecoteSingleBase, decimal DecoteCoupleThreshold, decimal DecoteCoupleBase,
    decimal DecoteRate, decimal MicroRentalCeiling, decimal MicroRentalAllowance, decimal RentalDeficitCap, decimal Pass,
    decimal FlatTaxRate, decimal CapitalSocialRate, decimal RentalSocialRate,
    decimal ChildcareCapPerChild, decimal ChildcareRate, decimal HomeEmploymentBase, decimal HomeEmploymentPerChild, decimal HomeEmploymentMax,
    decimal HomeEmploymentRate, decimal DonationAidCap, decimal DonationAidRate, decimal DonationRate, decimal DonationIncomeShare, decimal NicheCap)
{
    public bool CarriedForward => RevenueYear > RulesYear;

    public const int FirstYear = 2025;

    /// <summary>The rates and scales of a sale (sale tax spec §2), for this revenue year.</summary>
    public SaleTaxRules Sale => SaleTaxRules.For(RevenueYear);

    /// <summary>Revenus 2025: loi n° 2026-103 du 19 février 2026 (barème revalorisé de 0,9 %), LFSS 2026 for the capital levies of 2026.</summary>
    public static TaxRules For(int revenueYear)
    {
        if (revenueYear < FirstYear)
            throw new ArgumentOutOfRangeException(nameof(revenueYear), $"Rules start with the revenues of {FirstYear}.");
        return new TaxRules(
            revenueYear, 2025,
            [new(0m, 0m), new(11_600m, 0.11m), new(29_579m, 0.30m), new(84_577m, 0.41m), new(181_917m, 0.45m)],
            SalaryAllowanceRate: 0.10m, SalaryAllowanceMin: 509m, SalaryAllowanceMax: 14_555m,
            FamilyCapPerHalfShare: 1_807m, DecoteSingleThreshold: 1_982m, DecoteSingleBase: 897m, DecoteCoupleThreshold: 3_277m, DecoteCoupleBase: 1_483m,
            DecoteRate: 0.4525m, MicroRentalCeiling: 15_000m, MicroRentalAllowance: 0.30m, RentalDeficitCap: 10_700m, Pass: 47_100m,
            FlatTaxRate: 0.128m, CapitalSocialRate: revenueYear >= 2026 ? 0.186m : 0.172m, RentalSocialRate: 0.172m,
            ChildcareCapPerChild: 3_500m, ChildcareRate: 0.50m, HomeEmploymentBase: 12_000m, HomeEmploymentPerChild: 1_500m, HomeEmploymentMax: 15_000m,
            HomeEmploymentRate: 0.50m, DonationAidCap: 1_000m, DonationAidRate: 0.75m, DonationRate: 0.66m, DonationIncomeShare: 0.20m, NicheCap: 10_000m);
    }

    /// <summary>Employee contributions withheld from the gross, by status (spec §2): an approximation, the payslip's own rate varies.</summary>
    public static decimal ContributionRate(EmploymentStatus status) => status switch
    {
        EmploymentStatus.NonExecutive => 0.22m,
        EmploymentStatus.Executive => 0.25m,
        EmploymentStatus.Public => 0.18m,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// <summary>CSG non déductible (2,4 %) and CRDS (0,5 %) on 98,25 % of the gross: withheld, yet taxable.</summary>
    public const decimal NonDeductibleCsg = 0.029m * 0.9825m;
}
