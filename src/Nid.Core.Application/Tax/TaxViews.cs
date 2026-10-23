using Nid.Core.Contracts;
using Nid.Core.Domain.Tax;
using static Nid.Core.Application.Tax.TaxEstimates;

namespace Nid.Core.Application.Tax;

/// <summary>
/// Spec §8. <c>ProfileFrom</c>: the earlier year whose profile stands in, null when the year has its own (or none). <c>Dependents</c>
/// is the household's count of dependents (the profile may say otherwise); <c>RentalProperties</c> the visible ones Pinel may name.
/// </summary>
public sealed record TaxEstimateView(
    int Year, TaxProfileView Profile, int? ProfileFrom, TaxInputsView Inputs, TaxResultView Result, IReadOnlyList<TaxNoticeView> Notices,
    IReadOnlyList<TaxAdultView> Adults, int Dependents, IReadOnlyList<TaxPropertyView> RentalProperties) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Notices.Select(n => n.Item).Concat(RentalProperties.Select(p => p.Id));
}

/// <summary>
/// An item the estimate left out or could not compute: <c>missing_gross</c>, <c>unsupported_income</c>, <c>withdrawal_not_estimated</c>,
/// <c>other_currency</c>, <c>not_a_declarant</c>; a property's <c>rental_charges_removed</c>, a loan's <c>rental_fee_not_counted</c>.
/// </summary>
public sealed record TaxNoticeView(string Code, ItemId Item, string Name, string Amount);

/// <summary>
/// Rental 2044 spec §5. <c>Regime</c> <c>micro</c> or <c>real</c>; <c>Provisional</c> for the current year and after (the tenancies'
/// rents to come included); <c>ToCheck</c> the expenses « À vérifier », out of the totals.
/// </summary>
public sealed record Rental2044View(
    int Year, string Regime, bool Provisional, IReadOnlyList<Rental2044PropertyView> Properties, Rental2044TotalsView Totals,
    IReadOnlyList<Rental2044CheckView> ToCheck, IReadOnlyList<TaxNoticeView> Notices) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems()
        => Properties.Select(p => p.Id).Concat(ToCheck.Select(c => c.Property)).Concat(Notices.Select(n => n.Item));
}

/// <summary>A property's column: lines 211, 215, 221, 222, 223, 224, 227, 229, 230, 240, 250 and 263, in that order.</summary>
public sealed record Rental2044PropertyView(ItemId Id, string Name, IReadOnlyList<Rental2044LineView> Lines);

/// <summary><c>Line</c> is the form's number (<c>"211"</c>); <c>Amount</c> in whole euros.</summary>
public sealed record Rental2044LineView(string Line, string Amount);

/// <summary>
/// Spec §3: lines 420 (<c>Result</c>), 430 → 441 for a deficit, then the 2042's boxes to copy: 4BA the taxable income, 4BB the
/// deficit carried, 4BC the deficit on the global income (all <c>"0.00"</c> under the micro-foncier), 4BE the micro's gross rents.
/// </summary>
public sealed record Rental2044TotalsView(
    string Receipts, string Charges, string Interest, string Result, string InterestDeficit, string ChargesDeficit,
    string GlobalDeficit, string CarriedDeficit, string Box4BA, string Box4BB, string Box4BC, string Box4BE);

/// <summary>Spec §3: an expense « À vérifier » (<c>works</c>, <c>other</c>) of a property.</summary>
public sealed record Rental2044CheckView(ItemId Property, string Name, NodaTime.LocalDate OccurredOn, string Category, string? Note, string Amount);

public sealed record TaxAdultView(Guid Id, string DisplayName);

public sealed record TaxPropertyView(ItemId Id, string Name, int AcquiredIn);

/// <summary><c>ContributionRates</c> in percent (<c>"23.5"</c>); an adult absent from it or from <c>ActualExpenses</c> keeps the default.</summary>
public sealed record TaxProfileView(
    IReadOnlyDictionary<Guid, string> Statuses, int? Dependents, int ChildrenUnder6, string? RentalRegime, IReadOnlyList<PinelCommitment> Pinel,
    string ChildcareOutsideHome, string HomeEmployment, string Donations, string DonationsAid,
    IReadOnlyDictionary<Guid, string> ContributionRates, IReadOnlyDictionary<Guid, string> ActualExpenses)
{
    public static TaxProfileView Of(TaxProfile p) => new(
        p.Statuses, p.Dependents, p.ChildrenUnder6, p.RentalRegime, p.Pinel,
        Amount(p.ChildcareOutsideHome), Amount(p.HomeEmployment), Amount(p.Donations), Amount(p.DonationsAid),
        (p.ContributionRates ?? new Dictionary<Guid, decimal>()).ToDictionary(x => x.Key, x => Percent(x.Value)),
        (p.ActualExpenses ?? new Dictionary<Guid, decimal>()).ToDictionary(x => x.Key, x => Amount(x.Value)));
}

/// <summary><c>ContributionRate</c> in percent, null for the status's; <c>ActualExpenses</c> null for the 10 % allowance.</summary>
public sealed record TaxDeclarantView(
    string Name, string SalaryGross, string Status, string OtherTaxable, string PerContributions, string? ContributionRate = null, string? ActualExpenses = null);

/// <summary>What the calculation read, as the simulation sends it back once edited.</summary>
public sealed record TaxInputsView(
    int Year, IReadOnlyList<TaxDeclarantView> Declarants, int Dependents, int ChildrenUnder6,
    string RentalGross, string RentalInterest, string RentalCharges, string? RentalRegime,
    string CapitalIncome, string NonTaxableIncome, string PinelReduction,
    string ChildcareOutsideHome, string HomeEmployment, string Donations, string DonationsAid)
{
    public static TaxInputsView Of(TaxInputs i) => new(
        i.Year, i.Declarants.Select(d => new TaxDeclarantView(d.Name, Amount(d.SalaryGross), Wire(d.Status), Amount(d.OtherTaxable), Amount(d.PerContributions),
            d.ContributionRate is { } r ? Percent(r) : null, d.ActualExpenses is { } e ? Amount(e) : null)).ToList(),
        i.Dependents, i.ChildrenUnder6, Amount(i.RentalGross), Amount(i.RentalInterest), Amount(i.RentalCharges),
        i.RentalRegime is { } r ? Wire(r) : null, Amount(i.CapitalIncome), Amount(i.NonTaxableIncome), Amount(i.PinelReduction),
        Amount(i.ChildcareOutsideHome), Amount(i.HomeEmployment), Amount(i.Donations), Amount(i.DonationsAid));
}

public sealed record TaxDeclarantResultView(string Name, string NetSalary, string NetTaxableIncome, string Allowance, string Taxable, string PerDeducted);

/// <summary>Spec §4, each step's figure as a decimal string; rates in percent (<c>"30"</c>, <c>"4.27"</c>).</summary>
public sealed record TaxResultView(
    int Year, int RulesYear, string Parts, IReadOnlyList<TaxDeclarantResultView> Declarants,
    string RentalRegime, string RentalTaxable, string RentalDeficit, string NetTaxableIncome,
    string GrossTax, bool FamilyCapApplied, string Decote, string PinelReduction, string DonationReduction,
    string ChildcareCredit, string HomeEmploymentCredit, string NicheCapExcess,
    string IncomeTax, string CapitalFlatTax, string SocialLevies, string TotalTax,
    string MarginalRate, string? AverageRate, string NetBeforeTax, string NetAfterTax, string NetAfterTaxMonthly,
    IReadOnlyList<string> Warnings, WithholdingView Withholding)
{
    public static TaxResultView Of(TaxResult r) => new(
        r.Year, r.RulesYear, r.Parts.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
        r.Declarants.Select(d => new TaxDeclarantResultView(d.Name, Amount(d.NetSalary), Amount(d.NetTaxableIncome), Amount(d.Allowance), Amount(d.Taxable), Amount(d.PerDeducted))).ToList(),
        Wire(r.RentalRegime), Amount(r.RentalTaxable), Amount(r.RentalDeficit), Amount(r.NetTaxableIncome),
        Amount(r.GrossTax), r.FamilyCapApplied, Amount(r.Decote), Amount(r.PinelReduction), Amount(r.DonationReduction),
        Amount(r.ChildcareCredit), Amount(r.HomeEmploymentCredit), Amount(r.NicheCapExcess),
        Amount(r.IncomeTax), Amount(r.CapitalFlatTax), Amount(r.SocialLevies), Amount(r.TotalTax),
        Rate(r.MarginalRate), r.AverageRate is { } a ? Rate(a) : null, Amount(r.NetBeforeTax), Amount(r.NetAfterTax), Amount(r.NetAfterTaxMonthly),
        r.Warnings, WithholdingView.Of(r.Withholding));

    private static string Rate(decimal rate) => (rate * 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record DeclarantWithholdingView(string Name, string Salaries, string Monthly);

/// <summary>Spec §4bis. <c>Rate</c> in percent, to the tenth (<c>"4.5"</c>).</summary>
public sealed record WithholdingView(
    string Rate, string TaxBase, string Income, IReadOnlyList<DeclarantWithholdingView> Declarants,
    string RentalDeclared, string RentalMonthly, string Annual, string LeftOut, string Balance)
{
    public static WithholdingView Of(Withholding w) => new(
        w.Rate.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), Amount(w.TaxBase), Amount(w.Income),
        w.Declarants.Select(d => new DeclarantWithholdingView(d.Name, Amount(d.Salaries), Amount(d.Monthly))).ToList(),
        Amount(w.RentalDeclared), Amount(w.RentalMonthly), Amount(w.Annual), Amount(w.LeftOut), Amount(w.Balance));
}
