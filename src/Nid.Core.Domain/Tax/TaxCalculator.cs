namespace Nid.Core.Domain.Tax;

public enum EmploymentStatus { NonExecutive, Executive, Public }

public enum RentalRegime { Micro, Real }

/// <summary>
/// A declarant's year, in euros: the gross salary, other taxable income already net (unemployment), PER contributions.
/// <c>ContributionRate</c> (a fraction) replaces the status's flat rate when the payslip's is known; <c>ActualExpenses</c>, when
/// given, replace the 10 % allowance (frais réels). <c>PerUnusedCeiling</c>: the unused PER ceilings of the three previous years,
/// added to an estimated ceiling only. <c>SalaryTaxable</c>: the net taxable income (« net imposable ») entered on the payslips,
/// counted as it is beside the estimate from <c>SalaryGross</c>. Spec §4.2: <c>PerCeiling</c> is the avis d'imposition's PER
/// ceiling, already total; null, the ceiling is estimated on <c>PreviousYearIncome</c> (last year's taxable income). <c>PerExpiring</c>:
/// the part of the ceiling lost on 31 December. <c>ExemptIncome</c>: the exempt income the reference income counts (overtime, the
/// value-sharing bonus), outside the tax itself.
/// </summary>
public sealed record TaxDeclarant(
    string Name, decimal SalaryGross, EmploymentStatus Status, decimal OtherTaxable, decimal PerContributions,
    decimal? ContributionRate = null, decimal? ActualExpenses = null, decimal PerUnusedCeiling = 0m, decimal SalaryTaxable = 0m,
    decimal? PerCeiling = null, decimal PreviousYearIncome = 0m, decimal PerExpiring = 0m, decimal ExemptIncome = 0m)
{
    public decimal EffectiveContributionRate => ContributionRate ?? TaxRules.ContributionRate(Status);
}

/// <summary>
/// Tax estimate spec §4: everything the calculation reads, in euros for the revenue year. <c>RentalRegime</c> null: micro when the
/// rents allow it. <c>RentalCharges</c> already holds the 20 € per property. <c>PinelReduction</c> is the year's (<see cref="Pinel"/>).
/// <c>RentalDeficitCarried</c>: the earlier years' rental deficits not yet imputed, which only offset the year's rental income.
/// <c>PerPooling</c>: the spouses pool their PER ceilings (spec §4.3), a joint declaration only. <c>DonationsAid</c> is box 7UD,
/// <c>DonationsAidLate</c> 7UQ: the aid donations of 2025 made from 14 October. <c>RentalEnergyWorks</c>: the energy renovation works
/// among the rental charges, which raise the deficit's cap.
/// </summary>
public sealed record TaxInputs(
    int Year, IReadOnlyList<TaxDeclarant> Declarants, int Dependents, int ChildrenUnder6,
    decimal RentalGross, decimal RentalInterest, decimal RentalCharges, RentalRegime? RentalRegime,
    decimal CapitalIncome, decimal NonTaxableIncome, decimal PinelReduction,
    decimal ChildcareOutsideHome, decimal HomeEmployment, decimal Donations, decimal DonationsAid, decimal RentalDeficitCarried = 0m,
    bool PerPooling = false, decimal DonationsAidLate = 0m, decimal RentalEnergyWorks = 0m);

/// <summary>Where a declarant's salary comes from: none, the entered net taxable income, the gross estimated, or both.</summary>
public enum SalaryMethod { None, Entered, Estimated, Mixed }

/// <summary>Where a declarant's PER ceiling comes from (spec §4.2): the avis d'imposition, or estimated on last year's income.</summary>
public enum PerCeilingSource { Notice, Estimated }

/// <summary>
/// Spec §4.3, a declarant's PER: <c>Deducted</c> includes the part the spouse's free ceiling covers under pooling, <c>Excess</c> is
/// what stays undeducted, <c>Remaining</c> the declarant's own ceiling still free; pooled, the household's free ceiling (the same
/// figure on each declarant and in <see cref="PerHousehold.Remaining"/>). <c>Minimum</c>: what is still to pay before 31 December so
/// that <c>Expiring</c> is not lost, the year's own ceiling being used first; <c>MinimumEstimated</c> when that year's ceiling is
/// estimated on last year's income (a notice ceiling entered without the unused ceilings it holds).
/// </summary>
public sealed record PerDeclarant(
    decimal Ceiling, PerCeilingSource Source, decimal Expiring, decimal Contributions, decimal Deducted, decimal Excess, decimal Remaining,
    decimal Minimum = 0m, bool MinimumEstimated = false);

/// <summary>
/// <c>PerDeducted</c> is <c>Per.Deducted</c>, deducted from this declarant's income. <c>ExemptNet</c>: the exempt income after the
/// allowance, which the next year's PER ceiling counts with <c>Taxable</c>.
/// </summary>
public sealed record DeclarantResult(
    string Name, decimal NetSalary, decimal NetTaxableIncome, decimal Allowance, decimal Taxable, decimal PerDeducted,
    SalaryMethod Method = SalaryMethod.None, PerDeclarant? Per = null, decimal ExemptNet = 0m);

/// <summary>
/// Spec §4.3, the household's PER: <c>Remaining</c> the free ceiling left, <c>TaxSaved</c> the income tax without any PER deduction
/// minus the actual one, <c>RoomInBracket</c> what can still be deducted at the marginal rate (0 in the 0 % bracket).
/// </summary>
public sealed record PerHousehold(bool Pooled, decimal Remaining, decimal TaxSaved, decimal RoomInBracket);

/// <summary>
/// Spec §4, each step's figure. <c>IncomeTax</c> is negative for a refund; <c>AverageRate</c> null without taxable income.
/// <c>RentalTaxable</c> is after <c>RentalDeficitCarriedUsed</c>, the part of the earlier deficits the year's rents absorb.
/// <c>Per</c> is always set by <see cref="TaxCalculator.Compute"/> (nullable only for the record's default). <c>ReferenceIncome</c>:
/// the revenu fiscal de référence (art. 1417 IV CGI), the net taxable income plus the PER deduction, the flat-taxed capital income
/// and the exempt income after the 10 % allowance, to the euro below.
/// </summary>
public sealed record TaxResult(
    int Year, int RulesYear, decimal Parts, IReadOnlyList<DeclarantResult> Declarants,
    RentalRegime RentalRegime, decimal RentalTaxable, decimal RentalDeficitCarriedUsed, decimal RentalDeficit, decimal NetTaxableIncome,
    decimal GrossTax, bool FamilyCapApplied, decimal Decote, decimal PinelReduction, decimal DonationReduction,
    decimal ChildcareCredit, decimal HomeEmploymentCredit, decimal NicheCapExcess,
    decimal IncomeTax, decimal CapitalFlatTax, decimal SocialLevies, decimal TotalTax,
    decimal MarginalRate, decimal? AverageRate, decimal NetBeforeTax, decimal NetAfterTax, decimal NetAfterTaxMonthly,
    IReadOnlyList<string> Warnings, Withholding Withholding, PerHousehold? Per = null, decimal ReferenceIncome = 0m);

/// <summary>
/// Spec §4bis: the withholding rate the year's income gives, and what « Actualiser suite à une hausse ou une baisse de vos revenus »
/// asks per declarant. <c>Rate</c> in percent, to the tenth; <c>Balance</c> is the income tax minus what the rate withholds
/// (negative: paid back the year after, the reductions and credits the rate leaves out).
/// </summary>
public sealed record Withholding(
    decimal Rate, decimal TaxBase, decimal Income, IReadOnlyList<DeclarantWithholding> Declarants,
    decimal RentalDeclared, decimal RentalMonthly, decimal Annual, decimal LeftOut, decimal Balance);

/// <summary>A declarant's salaries to declare (net taxable, before the 10 % allowance) and the monthly amount withheld at the household rate.</summary>
public sealed record DeclarantWithholding(string Name, decimal Salaries, decimal Monthly);

public static class TaxWarnings
{
    public const string RulesCarriedForward = "rules_carried_forward";
    public const string MicroOverCeiling = "micro_over_ceiling";
    public const string PinelRequiresReal = "pinel_requires_real";
    public const string HighIncomeContribution = "high_income_contribution";
    /// <summary>The profile's contribution rate is far from the one the phases holding both gross and net taxable show.</summary>
    public const string ContributionRateDiffers = "contribution_rate_differs";
    /// <summary>Spec §4.4: a declarant's PER contributions exceed the ceiling, once per declarant concerned.</summary>
    public const string PerOverCeiling = "per_over_ceiling";
    /// <summary>Spec §4.4: a declarant with PER contributions has no notice ceiling, so it is estimated.</summary>
    public const string PerCeilingEstimated = "per_ceiling_estimated";
}

/// <summary>Tax estimate spec §4: a joint declaration (or a single one), pure and in exact decimals.</summary>
public static class TaxCalculator
{
    public static TaxResult Compute(TaxInputs inputs) => Compute(inputs, withTaxSaved: true);

    /// <summary><paramref name="withTaxSaved"/> false: the inner run without PER, which needs no tax saved of its own.</summary>
    private static TaxResult Compute(TaxInputs inputs, bool withTaxSaved)
    {
        if (inputs.Declarants.Count is < 1 or > 2)
            throw new ArgumentException("A declaration has one or two declarants.", nameof(inputs));
        var rules = TaxRules.For(inputs.Year);
        var warnings = new List<string>();
        if (rules.CarriedForward)
            warnings.Add(TaxWarnings.RulesCarriedForward);

        // Spec §4.3: the PER deduction is a household step, pooling needs both declarants.
        var pooled = inputs.PerPooling && inputs.Declarants.Count == 2;
        var (pers, perRemaining) = PerDeductions(inputs, pooled);
        var declarants = inputs.Declarants.Select((d, i) => Declarant(d, rules) with { PerDeducted = pers[i].Deducted, Per = pers[i] }).ToList();
        warnings.AddRange(pers.Where(p => p.Excess > 0m).Select(_ => TaxWarnings.PerOverCeiling));
        warnings.AddRange(pers.Where(p => p.Source == PerCeilingSource.Estimated && p.Contributions > 0m).Select(_ => TaxWarnings.PerCeilingEstimated));

        var pinelOwner = inputs.PinelReduction > 0m;
        var regime = inputs.RentalRegime ?? (inputs.RentalGross <= rules.MicroRentalCeiling && !pinelOwner ? RentalRegime.Micro : RentalRegime.Real);
        if (regime == RentalRegime.Micro && inputs.RentalGross > rules.MicroRentalCeiling)
        {
            warnings.Add(TaxWarnings.MicroOverCeiling);
            regime = RentalRegime.Real;
        }
        // A Pinel property shuts the micro-foncier for all the household's rents.
        if (regime == RentalRegime.Micro && pinelOwner)
        {
            warnings.Add(TaxWarnings.PinelRequiresReal);
            regime = RentalRegime.Real;
        }
        decimal rentalTaxable, deficit = 0m;
        if (regime == RentalRegime.Micro)
            rentalTaxable = inputs.RentalGross * (1 - rules.MicroRentalAllowance);
        else
        {
            rentalTaxable = Math.Max(0m, inputs.RentalGross - inputs.RentalInterest - inputs.RentalCharges);
            // Interest only offsets rents; the other charges' deficit goes on the global income, capped (the rest carried, not followed).
            deficit = Rental2044.GlobalDeficit(inputs.RentalGross, inputs.RentalCharges, inputs.RentalInterest,
                rules.DeficitCap(inputs.RentalEnergyWorks, inputs.RentalCharges));
        }
        // Earlier deficits offset the net rental income, micro or real, never the global income.
        var carriedUsed = Math.Min(rentalTaxable, Math.Max(0m, inputs.RentalDeficitCarried));
        rentalTaxable -= carriedUsed;

        var global = Math.Max(0m, declarants.Sum(d => d.Taxable) + rentalTaxable - deficit);
        var taxable = Math.Floor(Math.Max(0m, global - declarants.Sum(d => d.PerDeducted)));

        var baseParts = inputs.Declarants.Count;
        var parts = baseParts + ChildParts(inputs.Dependents);
        var withShares = Scale(rules, taxable / parts) * parts;
        var withoutChildren = Scale(rules, taxable / baseParts) * baseParts;
        var cap = rules.FamilyCapPerHalfShare * (parts - baseParts) * 2;
        var familyCapped = withoutChildren - withShares > cap;
        var grossTax = familyCapped ? withoutChildren - cap : withShares;

        var (threshold, decoteBase) = baseParts == 2
            ? (rules.DecoteCoupleThreshold, rules.DecoteCoupleBase)
            : (rules.DecoteSingleThreshold, rules.DecoteSingleBase);
        var decote = grossTax < threshold ? Math.Min(grossTax, Math.Max(0m, decoteBase - rules.DecoteRate * grossTax)) : 0m;
        var tax = grossTax - decote;

        // Reductions never go below zero; credits are paid back.
        var pinel = Math.Min(Math.Max(0m, inputs.PinelReduction), tax);
        tax -= pinel;
        // The aid donations at 75 % within their cap (2025: those before 14 October within the sub-cap), the rest at 66 % with the
        // others, within 20 % of the income; the 75 % ones stay outside the 20 % (BOI-IR-RICI-250-30 § 35).
        var aidGiven = Math.Max(0m, inputs.DonationsAid) + Math.Max(0m, inputs.DonationsAidLate);
        var early = rules.DonationAidEarlyCap is { } sub ? Math.Min(Math.Max(0m, inputs.DonationsAid), sub) : Math.Max(0m, inputs.DonationsAid);
        var aid = Math.Min(early + Math.Max(0m, inputs.DonationsAidLate), rules.DonationAidCap);
        var otherDonations = Math.Min(inputs.Donations + aidGiven - aid, rules.DonationIncomeShare * taxable);
        var donation = Math.Min(rules.DonationAidRate * aid + rules.DonationRate * otherDonations, tax);
        tax -= donation;
        var childcare = rules.ChildcareRate * Math.Min(inputs.ChildcareOutsideHome, rules.ChildcareCapPerChild * inputs.ChildrenUnder6);
        var homeCap = Math.Min(rules.HomeEmploymentBase + rules.HomeEmploymentPerChild * inputs.Dependents, rules.HomeEmploymentMax);
        var home = rules.HomeEmploymentRate * Math.Min(inputs.HomeEmployment, homeCap);
        var excess = Math.Max(0m, pinel + childcare + home - rules.NicheCap);
        var incomeTax = Math.Round(tax - childcare - home + excess, 0, MidpointRounding.AwayFromZero);

        var flat = Euros(rules.FlatTaxRate * inputs.CapitalIncome);
        var levies = Euros(rules.CapitalSocialRate * inputs.CapitalIncome + rules.RentalSocialRate * rentalTaxable);
        var total = incomeTax + flat + levies;

        if (taxable > 250_000m * baseParts)
            warnings.Add(TaxWarnings.HighIncomeContribution);

        var netBefore = declarants.Sum(d => d.NetSalary) + inputs.Declarants.Sum(d => d.OtherTaxable)
            + inputs.RentalGross + inputs.CapitalIncome + inputs.NonTaxableIncome;
        var netAfter = netBefore - total;

        // Spec §4.3: the tax the PER saves, by the same inputs without any contribution.
        var taxSaved = 0m;
        if (withTaxSaved && inputs.Declarants.Any(d => d.PerContributions > 0m))
        {
            var withoutPer = inputs with { Declarants = [.. inputs.Declarants.Select(d => d with { PerContributions = 0m })] };
            taxSaved = Math.Max(0m, Compute(withoutPer, withTaxSaved: false).IncomeTax - incomeTax);
        }
        var perHousehold = new PerHousehold(pooled, perRemaining, taxSaved, RoomInBracket(rules, taxable, parts));
        var reference = Math.Floor(taxable + declarants.Sum(d => d.PerDeducted) + Math.Max(0m, inputs.CapitalIncome)
            + inputs.Declarants.Sum(d => ExemptNet(d, rules)));
        return new TaxResult(
            inputs.Year, rules.RulesYear, parts, declarants, regime, Euros(rentalTaxable), Euros(carriedUsed), Euros(deficit), taxable,
            Euros(grossTax), familyCapped, Euros(decote), Euros(pinel), Euros(donation), Euros(childcare), Euros(home), Euros(excess),
            incomeTax, flat, levies, total, MarginalRate(rules, taxable / parts),
            taxable == 0m ? null : Math.Round(Math.Max(0m, incomeTax) / taxable, 4, MidpointRounding.AwayFromZero),
            Euros(netBefore), Euros(netAfter), Euros(netAfter / 12), warnings,
            Withhold(rules, declarants, rentalTaxable, grossTax - decote, pinel + donation + childcare + home - excess, incomeTax),
            perHousehold, reference);
    }

    /// <summary>
    /// Spec §4.2–§4.3: each declarant's ceiling (the notice's as it is, or estimated on last year's income with last year's PASS,
    /// plus the unused ceilings), then <c>min(contributions, ceiling)</c>; pooled, each excess goes against the other's free ceiling
    /// and every declarant's <c>Remaining</c> is the household's free ceiling, also returned on its own. The minimum to pay: the
    /// year's own ceiling is used first, then the oldest unused one, so keeping the expiring part takes both (spec §4.3).
    /// </summary>
    private static (IReadOnlyList<PerDeclarant> Declarants, decimal Remaining) PerDeductions(TaxInputs inputs, bool pooled)
    {
        // The floor and the cap as impots.gouv.fr prints them, to the euro (4 637 € and 37 094 € on the 2024 PASS).
        var passN1 = TaxRules.Pass(inputs.Year - 1);
        var (floor, cap) = (Math.Round(0.10m * passN1, 0, MidpointRounding.AwayFromZero), Math.Round(0.80m * passN1, 0, MidpointRounding.AwayFromZero));
        var own = inputs.Declarants.Select(d =>
        {
            var unused = Math.Max(0m, d.PerUnusedCeiling);
            var estimatedYear = Math.Max(Math.Min(0.10m * d.PreviousYearIncome, cap), floor);
            var ceiling = Euros(d.PerCeiling is { } notice ? Math.Max(0m, notice) : estimatedYear + unused);
            var expiring = Math.Min(Math.Max(0m, d.PerExpiring), ceiling);
            // The year's own ceiling: the notice's without its unused ceilings, or estimated when they are not known.
            var exact = d.PerCeiling is not null && unused > 0m;
            var year = exact ? Math.Max(0m, ceiling - unused) : d.PerCeiling is null ? estimatedYear : Math.Min(estimatedYear, ceiling - expiring);
            var contributions = Math.Max(0m, d.PerContributions);
            return (Ceiling: ceiling, Source: d.PerCeiling is null ? PerCeilingSource.Estimated : PerCeilingSource.Notice,
                Expiring: expiring, Contributions: contributions, Deducted: Math.Min(contributions, ceiling),
                KeepExpiring: Math.Min(Euros(year) + expiring, ceiling), YearEstimated: !exact);
        }).ToList();

        var covered = new decimal[own.Count];
        var lent = new decimal[own.Count];
        if (pooled)
            for (var i = 0; i < 2; i++)
            {
                var other = 1 - i;
                var transfer = Math.Min(own[i].Contributions - own[i].Deducted, own[other].Ceiling - own[other].Deducted - lent[other]);
                covered[i] = transfer;
                lent[other] += transfer;
            }

        var remaining = own.Select((p, i) => Euros(p.Ceiling - p.Deducted - lent[i])).ToList();
        var household = remaining.Sum();
        return (own.Select((p, i) => new PerDeclarant(
            p.Ceiling, p.Source, Euros(p.Expiring), Euros(p.Contributions), Euros(p.Deducted + covered[i]),
            Euros(p.Contributions - p.Deducted - covered[i]), pooled ? household : remaining[i],
            // Only while some of the expiring part is still free: without it, nothing is lost on 31 December.
            p.Expiring > 0m ? Euros(Math.Max(0m, p.KeepExpiring - p.Deducted - lent[i])) : 0m, p.YearEstimated)).ToList(), household);
    }

    /// <summary>
    /// Spec §4.3: <c>(RNI ÷ parts − the marginal bracket's floor) × parts</c>, to the euro below; 0 in the 0 % bracket. Under the
    /// family-quotient cap the tax follows the shares without the children, so this figure may overstate the room.
    /// </summary>
    private static decimal RoomInBracket(TaxRules rules, decimal taxable, decimal parts)
    {
        var bracket = MarginalBracket(rules, taxable / parts);
        return bracket is null || bracket.Rate == 0m ? 0m : Math.Floor((taxable / parts - bracket.From) * parts);
    }

    /// <summary>
    /// Spec §4bis, the DGFiP's « fiche de calcul du taux » : the tax after the family quotient and the decote, before reductions and
    /// credits, over the income within the withholding (salaries before the 10 % allowance, positive rental income). Capital income
    /// under the flat tax is outside it, and Nid holds no other income in the global one, so the tax needs no prorating.
    /// </summary>
    private static Withholding Withhold(TaxRules rules, IReadOnlyList<DeclarantResult> declarants, decimal rentalTaxable, decimal baseTax, decimal leftOut, decimal incomeTax)
    {
        var taxBase = Math.Round(baseTax, 0, MidpointRounding.AwayFromZero);
        var income = declarants.Sum(d => d.NetTaxableIncome) + Euros(rentalTaxable);
        var rate = income == 0m ? 0m : Math.Round(taxBase / income * 100, 1, MidpointRounding.AwayFromZero);
        var annual = Euros(rate / 100 * income);
        return new Withholding(
            rate, taxBase, income,
            declarants.Select(d => new DeclarantWithholding(d.Name, d.NetTaxableIncome, Euros(rate / 100 * d.NetTaxableIncome / 12))).ToList(),
            Euros(rentalTaxable), Euros((rate / 100 + rules.RentalSocialRate) * rentalTaxable / 12), annual, Euros(leftOut), incomeTax - annual);
    }

    /// <summary>2 shares for a couple; half a share for each of the first two children, a whole one from the third.</summary>
    public static decimal ChildParts(int children) => children <= 2 ? 0.5m * children : 1m + (children - 2);

    /// <summary>The progressive scale on one share's income.</summary>
    public static decimal Scale(TaxRules rules, decimal quotient)
    {
        var tax = 0m;
        for (var i = 0; i < rules.Brackets.Count; i++)
        {
            var upper = i + 1 < rules.Brackets.Count ? rules.Brackets[i + 1].From : decimal.MaxValue;
            tax += rules.Brackets[i].Rate * Math.Max(0m, Math.Min(quotient, upper) - rules.Brackets[i].From);
        }
        return tax;
    }

    public static decimal MarginalRate(TaxRules rules, decimal quotient) => MarginalBracket(rules, quotient)?.Rate ?? 0m;

    /// <summary>The bracket one share's income ends in; null at zero.</summary>
    private static TaxBracket? MarginalBracket(TaxRules rules, decimal quotient) => rules.Brackets.LastOrDefault(b => quotient > b.From);

    private static decimal NetSalary(TaxDeclarant d) => d.SalaryGross * (1 - d.EffectiveContributionRate) + d.SalaryTaxable;

    private static decimal Income(TaxDeclarant d) => NetSalary(d) + TaxRules.NonDeductibleCsg * d.SalaryGross + d.OtherTaxable;

    private static decimal Allowance(TaxDeclarant d, TaxRules rules, decimal income) => Math.Min(income, d.ActualExpenses ?? rules.SalaryAllowance(income));

    /// <summary>
    /// The exempt income as the reference income counts it: the notice's « revenus exonérés nets », after the 10 % allowance the
    /// overtime adds to the salaries' (within the ceiling). Under actual expenses it counts whole.
    /// </summary>
    private static decimal ExemptNet(TaxDeclarant d, TaxRules rules)
    {
        var exempt = Math.Max(0m, d.ExemptIncome);
        if (d.ActualExpenses is not null)
            return exempt;
        var income = Income(d);
        return exempt - (Allowance(d, rules, income + exempt) - Allowance(d, rules, income));
    }

    private static DeclarantResult Declarant(TaxDeclarant d, TaxRules rules)
    {
        var net = NetSalary(d);
        var income = Income(d);
        var method = (d.SalaryTaxable > 0m, d.SalaryGross > 0m) switch
        {
            (true, true) => SalaryMethod.Mixed,
            (true, false) => SalaryMethod.Entered,
            (false, true) => SalaryMethod.Estimated,
            _ => SalaryMethod.None,
        };
        var allowance = Allowance(d, rules, income);
        var taxable = income - allowance;
        // The PER deduction comes after, from the household step (PerDeductions).
        return new DeclarantResult(d.Name, Euros(net), Euros(income), Euros(allowance), Euros(taxable), 0m, method, ExemptNet: Euros(ExemptNet(d, rules)));
    }

    private static decimal Euros(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}

/// <summary>Tax estimate spec §5: a Pinel investment's reduction for one revenue year.</summary>
public static class Pinel
{
    public const decimal PriceCap = 300_000m;
    public const decimal PerSquareMetreCap = 5_500m;

    /// <summary>The cost price capped at 300 000 € and 5 500 €/m² (no surface: the first cap only).</summary>
    public static decimal Base(decimal costPrice, decimal? surfaceM2)
        => Math.Min(Math.Min(costPrice, PriceCap), surfaceM2 is { } s ? PerSquareMetreCap * s : decimal.MaxValue);

    /// <summary>Total rates for 6, 9 and 12 years by acquisition year; Pinel+ keeps the first ones.</summary>
    public static (decimal Six, decimal Nine, decimal Twelve) Rates(int acquisitionYear, bool plus) => (plus, acquisitionYear) switch
    {
        (true, _) or (_, <= 2022) => (0.12m, 0.18m, 0.21m),
        (_, 2023) => (0.105m, 0.15m, 0.175m),
        _ => (0.09m, 0.12m, 0.14m),
    };

    /// <summary>
    /// The initial commitment's rate spread over its years, then each 3-year extension's increase over its 3 years; nothing outside
    /// [startYear, startYear + totalYears). <paramref name="initialYears"/> is 6 or 9, <paramref name="totalYears"/> 6, 9 or 12.
    /// </summary>
    public static decimal AnnualReduction(decimal pinelBase, int acquisitionYear, bool plus, int initialYears, int totalYears, int startYear, int year)
    {
        if (initialYears is not (6 or 9) || totalYears is not (6 or 9 or 12) || totalYears < initialYears)
            throw new ArgumentOutOfRangeException(nameof(totalYears), "A Pinel commitment starts at 6 or 9 years and lasts 6, 9 or 12.");
        var k = year - startYear + 1;
        if (k < 1 || k > totalYears)
            return 0m;
        var (six, nine, twelve) = Rates(acquisitionYear, plus);
        decimal rate;
        if (k <= initialYears)
            rate = (initialYears == 6 ? six : nine) / initialYears;
        else if (k <= 9)
            rate = (nine - six) / 3;
        else
            rate = (twelve - nine) / 3;
        return Math.Round(pinelBase * rate, 2, MidpointRounding.AwayFromZero);
    }
}
