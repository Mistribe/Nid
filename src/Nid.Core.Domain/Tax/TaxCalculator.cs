namespace Nid.Core.Domain.Tax;

public enum EmploymentStatus { NonExecutive, Executive, Public }

public enum RentalRegime { Micro, Real }

/// <summary>
/// A declarant's year, in euros: the gross salary, other taxable income already net (unemployment), PER contributions.
/// <c>ContributionRate</c> (a fraction) replaces the status's flat rate when the payslip's is known; <c>ActualExpenses</c>, when
/// given, replace the 10 % allowance (frais réels). <c>PerUnusedCeiling</c>: the unused PER ceilings of the three previous years,
/// added to the year's.
/// </summary>
public sealed record TaxDeclarant(
    string Name, decimal SalaryGross, EmploymentStatus Status, decimal OtherTaxable, decimal PerContributions,
    decimal? ContributionRate = null, decimal? ActualExpenses = null, decimal PerUnusedCeiling = 0m)
{
    public decimal EffectiveContributionRate => ContributionRate ?? TaxRules.ContributionRate(Status);
}

/// <summary>
/// Tax estimate spec §4: everything the calculation reads, in euros for the revenue year. <c>RentalRegime</c> null: micro when the
/// rents allow it. <c>RentalCharges</c> already holds the 20 € per property. <c>PinelReduction</c> is the year's (<see cref="Pinel"/>).
/// <c>RentalDeficitCarried</c>: the earlier years' rental deficits not yet imputed, which only offset the year's rental income.
/// </summary>
public sealed record TaxInputs(
    int Year, IReadOnlyList<TaxDeclarant> Declarants, int Dependents, int ChildrenUnder6,
    decimal RentalGross, decimal RentalInterest, decimal RentalCharges, RentalRegime? RentalRegime,
    decimal CapitalIncome, decimal NonTaxableIncome, decimal PinelReduction,
    decimal ChildcareOutsideHome, decimal HomeEmployment, decimal Donations, decimal DonationsAid, decimal RentalDeficitCarried = 0m);

public sealed record DeclarantResult(string Name, decimal NetSalary, decimal NetTaxableIncome, decimal Allowance, decimal Taxable, decimal PerDeducted);

/// <summary>
/// Spec §4, each step's figure. <c>IncomeTax</c> is negative for a refund; <c>AverageRate</c> null without taxable income.
/// <c>RentalTaxable</c> is after <c>RentalDeficitCarriedUsed</c>, the part of the earlier deficits the year's rents absorb.
/// </summary>
public sealed record TaxResult(
    int Year, int RulesYear, decimal Parts, IReadOnlyList<DeclarantResult> Declarants,
    RentalRegime RentalRegime, decimal RentalTaxable, decimal RentalDeficitCarriedUsed, decimal RentalDeficit, decimal NetTaxableIncome,
    decimal GrossTax, bool FamilyCapApplied, decimal Decote, decimal PinelReduction, decimal DonationReduction,
    decimal ChildcareCredit, decimal HomeEmploymentCredit, decimal NicheCapExcess,
    decimal IncomeTax, decimal CapitalFlatTax, decimal SocialLevies, decimal TotalTax,
    decimal MarginalRate, decimal? AverageRate, decimal NetBeforeTax, decimal NetAfterTax, decimal NetAfterTaxMonthly,
    IReadOnlyList<string> Warnings, Withholding Withholding);

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
}

/// <summary>Tax estimate spec §4: a joint declaration (or a single one), pure and in exact decimals.</summary>
public static class TaxCalculator
{
    public static TaxResult Compute(TaxInputs inputs)
    {
        if (inputs.Declarants.Count is < 1 or > 2)
            throw new ArgumentException("A declaration has one or two declarants.", nameof(inputs));
        var rules = TaxRules.For(inputs.Year);
        var warnings = new List<string>();
        if (rules.CarriedForward)
            warnings.Add(TaxWarnings.RulesCarriedForward);

        var declarants = inputs.Declarants.Select(d => Declarant(d, rules)).ToList();

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
            deficit = Rental2044.GlobalDeficit(inputs.RentalGross, inputs.RentalCharges, inputs.RentalInterest, rules.RentalDeficitCap);
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
        var aid = Math.Min(inputs.DonationsAid, rules.DonationAidCap);
        var otherDonations = Math.Min(inputs.Donations + inputs.DonationsAid - aid, rules.DonationIncomeShare * taxable);
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
        return new TaxResult(
            inputs.Year, rules.RulesYear, parts, declarants, regime, Euros(rentalTaxable), Euros(carriedUsed), Euros(deficit), taxable,
            Euros(grossTax), familyCapped, Euros(decote), Euros(pinel), Euros(donation), Euros(childcare), Euros(home), Euros(excess),
            incomeTax, flat, levies, total, MarginalRate(rules, taxable / parts),
            taxable == 0m ? null : Math.Round(Math.Max(0m, incomeTax) / taxable, 4, MidpointRounding.AwayFromZero),
            Euros(netBefore), Euros(netAfter), Euros(netAfter / 12), warnings,
            Withhold(rules, declarants, rentalTaxable, grossTax - decote, pinel + donation + childcare + home - excess, incomeTax));
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

    public static decimal MarginalRate(TaxRules rules, decimal quotient)
        => rules.Brackets.LastOrDefault(b => quotient > b.From)?.Rate ?? 0m;

    private static DeclarantResult Declarant(TaxDeclarant d, TaxRules rules)
    {
        var net = d.SalaryGross * (1 - d.EffectiveContributionRate);
        var income = net + TaxRules.NonDeductibleCsg * d.SalaryGross + d.OtherTaxable;
        var allowance = Math.Min(income, d.ActualExpenses
            ?? Math.Min(rules.SalaryAllowanceMax, Math.Max(rules.SalaryAllowanceMin, rules.SalaryAllowanceRate * income)));
        var taxable = income - allowance;
        // shortcut: the year's ceiling uses this year's income and PASS, the real one the previous year's; upgrade if the avis d'imposition's figure is ever stored.
        var perCap = Math.Max(0.10m * Math.Min(taxable, 8 * rules.Pass), 0.10m * rules.Pass) + Math.Max(0m, d.PerUnusedCeiling);
        return new DeclarantResult(d.Name, Euros(net), Euros(income), Euros(allowance), Euros(taxable), Euros(Math.Min(Math.Max(0m, d.PerContributions), perCap)));
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
