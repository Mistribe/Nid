namespace Nid.Core.Domain.Tax;

/// <summary>One bracket of the progressive scale, per share: <c>Rate</c> applies from <c>From</c> up to the next bracket.</summary>
public sealed record TaxBracket(decimal From, decimal Rate);

/// <summary>
/// Tax estimate spec §3: the parameters of one revenue year. <c>RulesYear</c> is the year they were written for: a later revenue
/// year carries the last known scale forward (<see cref="CarriedForward"/>), only the capital social levies follow the LFSS.
/// <c>Provisional</c>: the year's figures come from the finance bill, not yet the law.
/// <c>RentalDeficitEnergyCap</c>: the cap the energy renovation works raise the deficit's to (null outside the period);
/// <c>DonationAidEarlyCap</c>: the sub-cap of the aid donations made before 14 October 2025 (null after 2025).
/// </summary>
public sealed record TaxRules(
    int RevenueYear, int RulesYear, IReadOnlyList<TaxBracket> Brackets,
    decimal SalaryAllowanceRate, decimal SalaryAllowanceMin, decimal SalaryAllowanceMax,
    decimal FamilyCapPerHalfShare, decimal DecoteSingleThreshold, decimal DecoteSingleBase, decimal DecoteCoupleThreshold, decimal DecoteCoupleBase,
    decimal DecoteRate, decimal MicroRentalCeiling, decimal MicroRentalAllowance, decimal RentalDeficitCap, decimal? RentalDeficitEnergyCap,
    decimal FlatTaxRate, decimal CapitalSocialRate, decimal RentalSocialRate,
    decimal ChildcareCapPerChild, decimal ChildcareRate, decimal HomeEmploymentBase, decimal HomeEmploymentPerChild, decimal HomeEmploymentMax,
    decimal HomeEmploymentRate, decimal DonationAidCap, decimal? DonationAidEarlyCap, decimal DonationAidRate, decimal DonationRate, decimal DonationIncomeShare, decimal NicheCap,
    decimal PensionAllowanceMin, decimal PensionAllowanceMax, decimal AlimonyAdultChildCap,
    decimal FamilyCapPerQuarterShare, decimal SingleParentCap, decimal DisabilityHalfShareCap,
    decimal AgeDisabilityLowIncome, decimal AgeDisabilityHighIncome, decimal AgeDisabilityAmount, bool Provisional = false)
{
    public bool CarriedForward => RevenueYear > RulesYear;

    public const int FirstYear = 2020;

    /// <summary>The last revenue year with its own scale: a later one carries it forward.</summary>
    public static int LatestYear => Scales.Keys.Max();

    /// <summary>The rates and scales of a sale (sale tax spec §2), for this revenue year.</summary>
    public SaleTaxRules Sale => SaleTaxRules.For(RevenueYear);

    /// <summary>The figures a revenue year's finance law sets (most indexed on the first bracket), one entry per year.</summary>
    private sealed record Scale(
        IReadOnlyList<TaxBracket> Brackets, decimal AllowanceMin, decimal AllowanceMax, decimal FamilyCap,
        decimal DecoteSingleThreshold, decimal DecoteSingleBase, decimal DecoteCoupleThreshold, decimal DecoteCoupleBase,
        decimal ChildcareCap, decimal HomeEmploymentBase, decimal HomeEmploymentPerChild, decimal HomeEmploymentMax,
        decimal PensionAllowanceMin, decimal PensionAllowanceMax, decimal AlimonyAdultChildCap,
        decimal QuarterShareCap, decimal SingleParentCap, decimal DisabilityHalfShareCap,
        decimal AgeDisabilityLowIncome, decimal AgeDisabilityHighIncome, decimal AgeDisabilityAmount, decimal DonationAidCap = 1_000m,
        bool Provisional = false);

    // The revenues of 2020 to 2024: each year's finance law (CGI art. 197 as it stood), the figures as OpenFisca France records them
    // with those references (openfisca_france/parameters/impot_revenu). The décote thresholds are its bases over 45,25 %, to the euro;
    // the quarter share's cap half the half share's and a disabled declarant's the half share's with its complementary reduction, as
    // for 2025. The childcare cap is 2 300 € before the 2022 revenues (CGI art. 200 quater B, LF 2023 art. 4); the aid donations'
    // 1 000 € from the 2020 revenues (LFR n° 2020-473 art. 14, extended to 2025). The 2019 revenues still had a 14 % bracket and the
    // 20 % reduction under income conditions: not a table entry.
    private static Scale Past(
        decimal b1, decimal b2, decimal b3, decimal b4, decimal allowanceMin, decimal allowanceMax, decimal familyCap, decimal postCapReduction,
        decimal decoteSingleBase, decimal decoteCoupleBase, decimal childcareCap, decimal pensionMin, decimal pensionMax, decimal alimonyCap,
        decimal singleParentCap, decimal ageLow, decimal ageHigh, decimal ageAmount)
        => new([new(0m, 0m), new(b1, 0.11m), new(b2, 0.30m), new(b3, 0.41m), new(b4, 0.45m)],
            AllowanceMin: allowanceMin, AllowanceMax: allowanceMax, FamilyCap: familyCap,
            DecoteSingleThreshold: Math.Round(decoteSingleBase / 0.4525m), DecoteSingleBase: decoteSingleBase,
            DecoteCoupleThreshold: Math.Round(decoteCoupleBase / 0.4525m), DecoteCoupleBase: decoteCoupleBase,
            ChildcareCap: childcareCap, HomeEmploymentBase: 12_000m, HomeEmploymentPerChild: 1_500m, HomeEmploymentMax: 15_000m,
            PensionAllowanceMin: pensionMin, PensionAllowanceMax: pensionMax, AlimonyAdultChildCap: alimonyCap,
            QuarterShareCap: familyCap / 2, SingleParentCap: singleParentCap, DisabilityHalfShareCap: familyCap + postCapReduction,
            AgeDisabilityLowIncome: ageLow, AgeDisabilityHighIncome: ageHigh, AgeDisabilityAmount: ageAmount);

    private static readonly Dictionary<int, Scale> Scales = new()
    {
        // Revenus 2020: loi n° 2020-1721 du 29 décembre 2020 (LF pour 2021), art. 2.
        [2020] = Past(10_084m, 25_710m, 73_516m, 158_122m, 442m, 12_652m, 1_570m, 1_565m, 779m, 1_289m, 2_300m, 394m, 3_858m, 5_959m, 3_704m, 15_340m, 24_690m, 2_448m),
        // Revenus 2021: loi n° 2021-1900 du 30 décembre 2021 (LF pour 2022), art. 2.
        [2021] = Past(10_225m, 26_070m, 74_545m, 160_336m, 448m, 12_829m, 1_592m, 1_587m, 790m, 1_307m, 2_300m, 400m, 3_912m, 6_042m, 3_756m, 15_560m, 25_040m, 2_484m),
        // Revenus 2022: loi n° 2022-1726 du 30 décembre 2022 (LF pour 2023), art. 2 and 4.
        [2022] = Past(10_777m, 27_478m, 78_570m, 168_994m, 472m, 13_522m, 1_678m, 1_673m, 833m, 1_378m, 3_500m, 422m, 4_123m, 6_368m, 3_959m, 16_410m, 26_400m, 2_620m),
        // Revenus 2023: loi n° 2023-1322 du 29 décembre 2023 (LF pour 2024), art. 2.
        [2023] = Past(11_294m, 28_797m, 82_341m, 177_106m, 495m, 14_171m, 1_759m, 1_753m, 873m, 1_444m, 3_500m, 442m, 4_321m, 6_674m, 4_149m, 17_200m, 27_670m, 2_746m),
        // Revenus 2024: loi n° 2025-127 du 14 février 2025 (LF pour 2025), art. 2.
        [2024] = Past(11_497m, 29_315m, 83_823m, 180_294m, 504m, 14_426m, 1_791m, 1_785m, 889m, 1_470m, 3_500m, 450m, 4_399m, 6_794m, 4_224m, 17_510m, 28_170m, 2_796m),
        // Revenus 2025: loi n° 2026-103 du 19 février 2026 (barème revalorisé de 0,9 %); the pensions' allowance and an adult
        // child's alimony cap as the brochure pratique IR 2026 prints them; the quarter share's cap (half the half share's, as the
        // official simulator applies it, not the 904 € service-public.gouv.fr rounds it to), box T's and a disabled
        // declarant's half share's (1 807 € and its 1 801 € complementary reduction) as service-public.gouv.fr gives them;
        // the allowance of art. 157 bis as BOI-IR-BASE-40 (7 April 2026) gives it.
        [2025] = new([new(0m, 0m), new(11_600m, 0.11m), new(29_579m, 0.30m), new(84_577m, 0.41m), new(181_917m, 0.45m)],
            AllowanceMin: 509m, AllowanceMax: 14_555m, FamilyCap: 1_807m,
            DecoteSingleThreshold: 1_982m, DecoteSingleBase: 897m, DecoteCoupleThreshold: 3_277m, DecoteCoupleBase: 1_483m,
            ChildcareCap: 3_500m, HomeEmploymentBase: 12_000m, HomeEmploymentPerChild: 1_500m, HomeEmploymentMax: 15_000m,
            PensionAllowanceMin: 454m, PensionAllowanceMax: 4_439m, AlimonyAdultChildCap: 6_855m,
            QuarterShareCap: 903.5m, SingleParentCap: 4_262m, DisabilityHalfShareCap: 3_608m,
            AgeDisabilityLowIncome: 17_670m, AgeDisabilityHighIncome: 28_430m, AgeDisabilityAmount: 2_822m, DonationAidCap: 2_000m),
        // Revenus 2026: projet de loi de finances pour 2027 n° 3210 du 1er octobre 2026, art. 2 (barème revalorisé de 2,1 %).
        // Provisional until the law. The 10 % allowance follows the first bracket (CGI art. 83 3°), the décote thresholds are
        // its bases over 45,25 %. The pensions' allowance, the adult child's alimony cap and the
        // family quotient's caps follow it too (CGI art. 158 5 a, 196 B, 197 I 2), and the allowance of art. 157 bis, its incomes
        // to the ten euros above, its amount to the euro above.
        [2026] = new([new(0m, 0m), new(11_844m, 0.11m), new(30_200m, 0.30m), new(86_353m, 0.41m), new(185_737m, 0.45m)],
            AllowanceMin: 520m, AllowanceMax: 14_861m, FamilyCap: 1_845m,
            DecoteSingleThreshold: 2_022m, DecoteSingleBase: 915m, DecoteCoupleThreshold: 3_344m, DecoteCoupleBase: 1_513m,
            ChildcareCap: 3_500m, HomeEmploymentBase: 12_000m, HomeEmploymentPerChild: 1_500m, HomeEmploymentMax: 15_000m,
            PensionAllowanceMin: 464m, PensionAllowanceMax: 4_532m, AlimonyAdultChildCap: 6_999m,
            QuarterShareCap: 922.5m, SingleParentCap: 4_352m, DisabilityHalfShareCap: 3_684m,
            AgeDisabilityLowIncome: 18_050m, AgeDisabilityHighIncome: 29_030m, AgeDisabilityAmount: 2_882m, DonationAidCap: 2_000m, Provisional: true),
    };

    /// <summary>
    /// The scale of <paramref name="revenueYear"/> (its entry in <c>Scales</c>, the latest carried forward); LFSS 2026 for the capital levies
    /// of 2026. The aid donations' cap is 2 000 € from 14 October 2025, 1 000 € before within it (art. 28 of loi n° 2026-103,
    /// BOI-IR-RICI-250-30 § 30). The deficit's 21 400 € needs works paid from 2023 to 2025 (CGI art. 156 I 3°, ann. III art. 41 DO).
    /// </summary>
    public static TaxRules For(int revenueYear)
    {
        if (revenueYear < FirstYear)
            throw new ArgumentOutOfRangeException(nameof(revenueYear), $"Rules start with the revenues of {FirstYear}.");
        var rulesYear = Math.Min(revenueYear, LatestYear);
        var s = Scales[rulesYear];
        return new TaxRules(
            revenueYear, rulesYear, s.Brackets,
            SalaryAllowanceRate: 0.10m, SalaryAllowanceMin: s.AllowanceMin, SalaryAllowanceMax: s.AllowanceMax,
            FamilyCapPerHalfShare: s.FamilyCap, DecoteSingleThreshold: s.DecoteSingleThreshold, DecoteSingleBase: s.DecoteSingleBase,
            DecoteCoupleThreshold: s.DecoteCoupleThreshold, DecoteCoupleBase: s.DecoteCoupleBase,
            DecoteRate: 0.4525m, MicroRentalCeiling: 15_000m, MicroRentalAllowance: 0.30m, RentalDeficitCap: 10_700m,
            RentalDeficitEnergyCap: revenueYear is >= 2023 and <= 2025 ? 21_400m : null,
            FlatTaxRate: 0.128m, CapitalSocialRate: revenueYear >= 2026 ? 0.186m : 0.172m, RentalSocialRate: 0.172m,
            ChildcareCapPerChild: s.ChildcareCap, ChildcareRate: 0.50m,
            HomeEmploymentBase: s.HomeEmploymentBase, HomeEmploymentPerChild: s.HomeEmploymentPerChild, HomeEmploymentMax: s.HomeEmploymentMax,
            HomeEmploymentRate: 0.50m, DonationAidCap: s.DonationAidCap,
            DonationAidEarlyCap: revenueYear == 2025 ? 1_000m : null, DonationAidRate: 0.75m, DonationRate: 0.66m, DonationIncomeShare: 0.20m, NicheCap: 10_000m,
            PensionAllowanceMin: s.PensionAllowanceMin, PensionAllowanceMax: s.PensionAllowanceMax, AlimonyAdultChildCap: s.AlimonyAdultChildCap,
            FamilyCapPerQuarterShare: s.QuarterShareCap, SingleParentCap: s.SingleParentCap, DisabilityHalfShareCap: s.DisabilityHalfShareCap,
            AgeDisabilityLowIncome: s.AgeDisabilityLowIncome, AgeDisabilityHighIncome: s.AgeDisabilityHighIncome, AgeDisabilityAmount: s.AgeDisabilityAmount,
            Provisional: s.Provisional);
    }

    /// <summary>The 10 % deduction on salaries: at least the minimum, at most the maximum, never more than the salaries themselves.</summary>
    public decimal SalaryAllowance(decimal income)
        => Math.Min(income, Math.Min(SalaryAllowanceMax, Math.Max(SalaryAllowanceMin, SalaryAllowanceRate * income)));

    /// <summary>
    /// The 10 % deduction on pensions (CGI art. 158-5 a): at least the minimum for each pensioner, never more than the pensions, and
    /// at most the maximum for the whole household, shared in proportion to each one's.
    /// </summary>
    public IReadOnlyList<decimal> PensionAllowances(IReadOnlyList<decimal> pensions)
    {
        var own = pensions.Select(p => Math.Min(Math.Max(0m, p), Math.Max(PensionAllowanceMin, PensionAllowanceRate * p))).ToList();
        var total = own.Sum();
        return total > PensionAllowanceMax ? [.. own.Select(a => PensionAllowanceMax * a / total)] : own;
    }

    public const decimal PensionAllowanceRate = 0.10m;

    /// <summary>
    /// CGI art. 157 bis: off the net global income, the full amount for each of the <paramref name="qualifying"/> declarants (65 or
    /// over, or disabled) up to the low income, half of it up to the high one, nothing above; never more than the income.
    /// </summary>
    public decimal AgeDisabilityAllowance(decimal netGlobal, int qualifying)
    {
        var each = netGlobal <= AgeDisabilityLowIncome ? AgeDisabilityAmount : netGlobal <= AgeDisabilityHighIncome ? AgeDisabilityAmount / 2 : 0m;
        return Math.Min(Math.Max(0m, netGlobal), each * qualifying);
    }

    /// <summary>
    /// The deficit the global income takes (BOI-RFPI-BASE-30-20): 10 700 €, raised by the energy renovation works among the charges
    /// up to 21 400 € in the period.
    /// </summary>
    public decimal DeficitCap(decimal energyWorks, decimal charges) => RentalDeficitEnergyCap is { } raised
        ? Math.Min(raised, RentalDeficitCap + Math.Min(Math.Max(0m, energyWorks), Math.Max(0m, charges)))
        : RentalDeficitCap;

    /// <summary>
    /// The annual social security ceiling (PASS) of <paramref name="year"/>, as the PER ceilings use it: the 2024 one gives the 4 637 € and
    /// 37 094 € of 2025, the 2025 one the 4 710 € and 37 680 € of 2026 (impots.gouv.fr). 2026: arrêté du 22 décembre 2025.
    /// An earlier year reads the first one, a later year carries the last one forward.
    /// </summary>
    public static decimal Pass(int year) => Passes[Math.Clamp(year, Passes.Keys.Min(), Passes.Keys.Max())];

    private static readonly Dictionary<int, decimal> Passes = new()
    {
        [2019] = 40_524m, [2020] = 41_136m, [2021] = 41_136m, [2022] = 41_136m, [2023] = 43_992m, [2024] = 46_368m, [2025] = 47_100m, [2026] = 48_060m,
    };

    /// <summary>Employee contributions withheld from the gross, by status (spec §2): an approximation, the payslip's own rate varies.</summary>
    public static decimal ContributionRate(EmploymentStatus status) => status switch
    {
        EmploymentStatus.NonExecutive => 0.22m,
        EmploymentStatus.Executive => 0.25m,
        EmploymentStatus.Public => 0.18m,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// <summary>#234: the allowance on eligible dividends under the progressive scale (art. 158, 3-2° CGI).</summary>
    public const decimal DividendAllowanceRate = 0.40m;

    /// <summary>#234: the CSG on capital income taxed at the scale, deductible from the next year's income (art. 154 quinquies CGI).</summary>
    public const decimal DeductibleCsgRate = 0.068m;

    /// <summary>
    /// The social levies on the gains declared in 3VG: patrimony income, taxed on the declaration, so the LFSS 2026's 18,6 % already
    /// reaches the 2025 gains, while the interest and dividends withheld in 2025 stay at 17,2 % (art. 12 of loi n° 2025-1403), as do
    /// the gains of earlier years.
    /// </summary>
    public static decimal GainsSocialRate(int revenueYear) => revenueYear >= 2025 ? 0.186m : 0.172m;

    /// <summary>CSG non déductible (2,4 %) and CRDS (0,5 %) on 98,25 % of the gross: withheld, yet taxable.</summary>
    public const decimal NonDeductibleCsg = 0.029m * 0.9825m;
}
