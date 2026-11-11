using Nid.Core.Contracts;
using Nid.Core.Domain.Tax;
using static Nid.Core.Application.Tax.TaxEstimates;

namespace Nid.Core.Application.Tax;

/// <summary>
/// Spec §8. <c>ProfileFrom</c>: the earlier year whose profile stands in, null when the year has its own (or none). <c>Dependents</c>
/// is the household's count of dependents (the profile may say otherwise); <c>RentalProperties</c> the visible ones Pinel may name.
/// Tax assessments spec §5-§6: <c>CarriedFrom</c> what the previous year's notices lay over the profile (null: nothing), <c>Gaps</c> the
/// year's own notice against the estimate (null: no notice for these adults).
/// </summary>
public sealed record TaxEstimateView(
    int Year, TaxProfileView Profile, int? ProfileFrom, TaxInputsView Inputs, TaxResultView Result, IReadOnlyList<TaxNoticeView> Notices,
    IReadOnlyList<TaxAdultView> Adults, int Dependents, IReadOnlyList<TaxPropertyView> RentalProperties,
    IReadOnlyList<TaxSalaryDetailView> SalaryDetail, IReadOnlyDictionary<Guid, string> ObservedRates,
    TaxCarryView? CarriedFrom = null, TaxGapsView? Gaps = null) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Notices.Select(n => n.Item).Concat(RentalProperties.Select(p => p.Id)).Concat(SalaryDetail.Select(d => d.Item));
}

/// <summary>
/// Tax-per spec §3.5: a payment of a person's salary, with the figure the estimate retained: <c>Category</c> <c>entered</c> (the net
/// taxable income), <c>gross</c> or <c>net</c>. Shown, outside every total.
/// </summary>
public sealed record TaxSalaryDetailView(Guid Person, ItemId Item, string Name, NodaTime.LocalDate On, string? Note, string Category, string Amount);

/// <summary>
/// An item the estimate left out or could not compute: <c>missing_gross</c>, <c>unsupported_income</c>, <c>withdrawal_not_estimated</c>,
/// <c>other_currency</c>, <c>not_a_declarant</c>; a property's <c>rental_charges_removed</c>, a loan's <c>rental_fee_not_counted</c>.
/// For <c>contribution_rate_differs</c> (a person's typed contribution rate strays from the one seen on their payslips) <c>Amount</c> is a
/// percent (<c>"22.85"</c>), not euros.
/// </summary>
public sealed record TaxNoticeView(string Code, ItemId Item, string Name, string Amount, int? MonthsValidated = null, int? MonthsEstimated = null);

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

/// <summary>
/// <c>ContributionRates</c> in percent (<c>"23.5"</c>); an adult absent from it, <c>ActualExpenses</c> or <c>PerUnusedCeilings</c> keeps the default.
/// <c>PerCeilings</c> and <c>PerCeilingsExpiring</c>: the notice's PER ceiling and the part lost on 31 December, by adult; an adult
/// absent from <c>PerCeilings</c> has an estimated ceiling. <c>ExemptIncomes</c>: the exempt income the reference income counts, by adult.
/// <c>DonationsAidLate</c>: box 7UQ; <c>RentalEnergyWorks</c>: the energy renovation works among the rental charges.
/// </summary>
public sealed record TaxProfileView(
    IReadOnlyDictionary<Guid, string> Statuses, int? Dependents, int ChildrenUnder6, string? RentalRegime, IReadOnlyList<PinelCommitment> Pinel,
    string ChildcareOutsideHome, string HomeEmployment, string Donations, string DonationsAid,
    IReadOnlyDictionary<Guid, string> ContributionRates, IReadOnlyDictionary<Guid, string> ActualExpenses,
    IReadOnlyDictionary<Guid, string> PerUnusedCeilings, string RentalDeficitCarried,
    IReadOnlyDictionary<Guid, string> PerCeilings, IReadOnlyDictionary<Guid, string> PerCeilingsExpiring, bool PerPooling,
    IReadOnlyDictionary<Guid, string> ExemptIncomes, string DonationsAidLate, string RentalEnergyWorks, string? Department)
{
    public static TaxProfileView Of(TaxProfile p) => new(
        p.Statuses, p.Dependents, p.ChildrenUnder6, p.RentalRegime, p.Pinel,
        Amount(p.ChildcareOutsideHome), Amount(p.HomeEmployment), Amount(p.Donations), Amount(p.DonationsAid),
        (p.ContributionRates ?? new Dictionary<Guid, decimal>()).ToDictionary(x => x.Key, x => Percent(x.Value)),
        (p.ActualExpenses ?? new Dictionary<Guid, decimal>()).ToDictionary(x => x.Key, x => Amount(x.Value)),
        (p.PerUnusedCeilings ?? new Dictionary<Guid, decimal>()).ToDictionary(x => x.Key, x => Amount(x.Value)), Amount(p.RentalDeficitCarried),
        (p.PerCeilings ?? new Dictionary<Guid, decimal>()).ToDictionary(x => x.Key, x => Amount(x.Value)),
        (p.PerCeilingsExpiring ?? new Dictionary<Guid, decimal>()).ToDictionary(x => x.Key, x => Amount(x.Value)), p.PerPooling,
        (p.ExemptIncomes ?? new Dictionary<Guid, decimal>()).ToDictionary(x => x.Key, x => Amount(x.Value)),
        Amount(p.DonationsAidLate), Amount(p.RentalEnergyWorks), p.Department);
}

/// <summary>
/// <c>ContributionRate</c> in percent, null for the status's; <c>ActualExpenses</c> null for the 10 % allowance; <c>SalaryTaxable</c>
/// the net taxable income entered, counted as it is beside the estimate from <c>SalaryGross</c>. Tax-per spec §4: <c>PerCeiling</c> the
/// notice's ceiling, null for an estimated one; <c>PreviousYearIncome</c> last year's taxable income (the estimate's basis);
/// <c>PerExpiring</c> the part of the ceiling lost on 31 December; <c>PerEnvelopes</c> the names of the PERs the contributions come from.
/// <c>ExemptIncome</c>: the exempt income the reference income counts.
/// </summary>
public sealed record TaxDeclarantView(
    string Name, string SalaryGross, string Status, string OtherTaxable, string PerContributions, string PerUnusedCeiling,
    string? ContributionRate = null, string? ActualExpenses = null, string SalaryTaxable = "0.00",
    string? PerCeiling = null, string PreviousYearIncome = "0.00", string PerExpiring = "0.00", IReadOnlyList<string>? PerEnvelopes = null,
    string ExemptIncome = "0.00");

/// <summary>What the calculation read, as the simulation sends it back once edited (<c>DonationsAidLate</c>: box 7UQ).</summary>
public sealed record TaxInputsView(
    int Year, IReadOnlyList<TaxDeclarantView> Declarants, int Dependents, int ChildrenUnder6,
    string RentalGross, string RentalInterest, string RentalCharges, string? RentalRegime,
    string CapitalIncome, string NonTaxableIncome, string PinelReduction,
    string ChildcareOutsideHome, string HomeEmployment, string Donations, string DonationsAid, string RentalDeficitCarried, bool PerPooling = false,
    string DonationsAidLate = "0.00", string RentalEnergyWorks = "0.00")
{
    /// <summary><paramref name="perEnvelopes"/>: the PERs' names by declarant, in the order of the declarants.</summary>
    public static TaxInputsView Of(TaxInputs i, IReadOnlyList<IReadOnlyList<string>>? perEnvelopes = null) => new(
        i.Year, i.Declarants.Select((d, n) => new TaxDeclarantView(d.Name, Amount(d.SalaryGross), Wire(d.Status), Amount(d.OtherTaxable), Amount(d.PerContributions), Amount(d.PerUnusedCeiling),
            d.ContributionRate is { } r ? Percent(r) : null, d.ActualExpenses is { } e ? Amount(e) : null, Amount(d.SalaryTaxable),
            d.PerCeiling is { } c ? Amount(c) : null, Amount(d.PreviousYearIncome), Amount(d.PerExpiring),
            perEnvelopes is not null && n < perEnvelopes.Count ? perEnvelopes[n] : [], Amount(d.ExemptIncome))).ToList(),
        i.Dependents, i.ChildrenUnder6, Amount(i.RentalGross), Amount(i.RentalInterest), Amount(i.RentalCharges),
        i.RentalRegime is { } r ? Wire(r) : null, Amount(i.CapitalIncome), Amount(i.NonTaxableIncome), Amount(i.PinelReduction),
        Amount(i.ChildcareOutsideHome), Amount(i.HomeEmployment), Amount(i.Donations), Amount(i.DonationsAid), Amount(i.RentalDeficitCarried), i.PerPooling,
        Amount(i.DonationsAidLate), Amount(i.RentalEnergyWorks));
}

/// <summary><c>Method</c>: how the salary was counted, <c>entered</c> (net taxable income), <c>estimated</c> (from the gross), <c>mixed</c>; null without salary.</summary>
public sealed record TaxDeclarantResultView(string Name, string NetSalary, string NetTaxableIncome, string Allowance, string Taxable, string PerDeducted, string? Method = null, Guid? Person = null, TaxDeclarantPerView? Per = null);

/// <summary>
/// Tax-per spec §4.2: a declarant's PER, in euros. <c>Source</c> <c>notice</c> (the avis d'imposition's ceiling) or <c>estimated</c> (on
/// last year's income); <c>Remaining</c> is the household's under pooling. Spec §4.3: <c>Minimum</c> what to pay before 31 December
/// so that <c>Expiring</c> is not lost, <c>MinimumEstimated</c> when the year's own ceiling in it is estimated.
/// </summary>
public sealed record TaxDeclarantPerView(
    string Ceiling, string Source, string Expiring, string Contributions, string Deducted, string Excess, string Remaining, string Minimum, bool MinimumEstimated)
{
    public static TaxDeclarantPerView Of(PerDeclarant p) => new(
        Amount(p.Ceiling), p.Source == PerCeilingSource.Notice ? "notice" : "estimated", Amount(p.Expiring), Amount(p.Contributions),
        Amount(p.Deducted), Amount(p.Excess), Amount(p.Remaining), Amount(p.Minimum), p.MinimumEstimated);
}

/// <summary>Tax-per spec §4.3: the household's PER. <c>TaxSaved</c> the tax the deductions save; <c>RoomInBracket</c> the income left in the marginal bracket.</summary>
public sealed record TaxPerView(bool Pooled, string Remaining, string TaxSaved, string RoomInBracket)
{
    public static TaxPerView Of(PerHousehold p) => new(p.Pooled, Amount(p.Remaining), Amount(p.TaxSaved), Amount(p.RoomInBracket));
}

/// <summary>
/// Spec §4, each step's figure as a decimal string; rates in percent (<c>"30"</c>, <c>"4.27"</c>). <c>ReferenceIncome</c>: the
/// revenu fiscal de référence. <c>RentalRegimeGap</c>: the total tax under the other rental regime minus this one, null when there is no choice.
/// </summary>
public sealed record TaxResultView(
    int Year, int RulesYear, string Parts, IReadOnlyList<TaxDeclarantResultView> Declarants,
    string RentalRegime, string RentalTaxable, string RentalDeficitCarriedUsed, string RentalDeficit, string NetTaxableIncome,
    string GrossTax, bool FamilyCapApplied, string Decote, string PinelReduction, string DonationReduction,
    string ChildcareCredit, string HomeEmploymentCredit, string NicheCapExcess,
    string IncomeTax, string CapitalFlatTax, string SocialLevies, string TotalTax,
    string MarginalRate, string? AverageRate, string NetBeforeTax, string NetAfterTax, string NetAfterTaxMonthly,
    IReadOnlyList<string> Warnings, WithholdingView Withholding, TaxPerView? Per = null, string ReferenceIncome = "0.00", string? RentalRegimeGap = null)
{
    /// <summary><paramref name="persons"/>: the adults' ids in the order of the declarants (the estimate's); a simulation passes none.</summary>
    public static TaxResultView Of(TaxResult r, IReadOnlyList<Guid>? persons = null) => new(
        r.Year, r.RulesYear, r.Parts.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
        r.Declarants.Select((d, i) => new TaxDeclarantResultView(d.Name, Amount(d.NetSalary), Amount(d.NetTaxableIncome), Amount(d.Allowance), Amount(d.Taxable), Amount(d.PerDeducted), WireMethod(d.Method), persons is not null && i < persons.Count ? persons[i] : null,
            d.Per is { } per ? TaxDeclarantPerView.Of(per) : null)).ToList(),
        Wire(r.RentalRegime), Amount(r.RentalTaxable), Amount(r.RentalDeficitCarriedUsed), Amount(r.RentalDeficit), Amount(r.NetTaxableIncome),
        Amount(r.GrossTax), r.FamilyCapApplied, Amount(r.Decote), Amount(r.PinelReduction), Amount(r.DonationReduction),
        Amount(r.ChildcareCredit), Amount(r.HomeEmploymentCredit), Amount(r.NicheCapExcess),
        Amount(r.IncomeTax), Amount(r.CapitalFlatTax), Amount(r.SocialLevies), Amount(r.TotalTax),
        Rate(r.MarginalRate), r.AverageRate is { } a ? Rate(a) : null, Amount(r.NetBeforeTax), Amount(r.NetAfterTax), Amount(r.NetAfterTaxMonthly),
        r.Warnings, WithholdingView.Of(r.Withholding), r.Per is { } household ? TaxPerView.Of(household) : null, Amount(r.ReferenceIncome),
        r.RentalRegimeGap is { } gap ? Amount(gap) : null);

    private static string? WireMethod(SalaryMethod method) => method switch
    {
        SalaryMethod.Entered => "entered",
        SalaryMethod.Estimated => "estimated",
        SalaryMethod.Mixed => "mixed",
        _ => null,
    };

    private static string Rate(decimal rate) => (rate * 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record DeclarantWithholdingView(string Name, string Salaries, string Monthly);

/// <summary>Spec §4bis. <c>Rate</c> in percent, to the tenth (<c>"4.5"</c>).</summary>
public sealed record WithholdingView(
    string Rate, string TaxBase, string Income, IReadOnlyList<DeclarantWithholdingView> Declarants,
    string RentalDeclared, string RentalMonthly, string Annual, string LeftOut, string Balance, WithholdingCutView? Cut = null)
{
    public static WithholdingView Of(Withholding w) => new(
        TenthRate(w.Rate), Amount(w.TaxBase), Amount(w.Income), DeclarantViews(w.Declarants),
        Amount(w.RentalDeclared), Amount(w.RentalMonthly), Amount(w.Annual), Amount(w.LeftOut), Amount(w.Balance),
        w.Cut is { } c ? new WithholdingCutView(TenthRate(c.Rate), DeclarantViews(c.Declarants), Amount(c.RentalMonthly), Amount(c.Annual), Amount(c.Less), Amount(c.Balance)) : null);

    private static string TenthRate(decimal rate) => rate.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    private static List<DeclarantWithholdingView> DeclarantViews(IReadOnlyList<DeclarantWithholding> declarants)
        => declarants.Select(d => new DeclarantWithholdingView(d.Name, Amount(d.Salaries), Amount(d.Monthly))).ToList();
}

/// <summary>Spec §4bis, the « baisse sans risque ». <c>Declarants[i].Salaries</c>: the salaries to enter.</summary>
public sealed record WithholdingCutView(
    string Rate, IReadOnlyList<DeclarantWithholdingView> Declarants, string RentalMonthly, string Annual, string Less, string Balance);

/// <summary>Tax assessments spec §5: what the notices of <c>Year</c> handed to the estimate, in euros; only the figures they print. <c>RentalDeficitCarried</c> is a suggestion for the profile's field, not laid over the estimate.</summary>
public sealed record TaxCarryView(
    int Year, IReadOnlyDictionary<Guid, string> PerCeilings, IReadOnlyDictionary<Guid, string> PerCeilingsExpiring,
    IReadOnlyDictionary<Guid, string> PerUnusedCeilings, string? RentalDeficitCarried)
{
    public static TaxCarryView Of(TaxCarryOver c) => new(
        c.Year - 1, c.PerCeilings.ToDictionary(x => x.Key, x => Amount(x.Value)), c.PerCeilingsExpiring.ToDictionary(x => x.Key, x => Amount(x.Value)),
        c.PerUnusedCeilings.ToDictionary(x => x.Key, x => Amount(x.Value)), c.RentalDeficitCarried is { } d ? Amount(d) : null);
}

/// <summary>Tax assessments spec §6: one compared figure, <c>Gap</c> the notice minus Nid; <c>Nid</c> and <c>Gap</c> null for a line Nid does not compute.</summary>
public sealed record TaxGapLineView(string Code, string Notice, string? Nid, string? Gap);

public sealed record TaxDeclarantGapView(Guid Person, string Notice, string Nid, string Gap);

/// <summary>Tax assessments spec §6: the notice <c>Assessment</c> against the estimate of its year, computed by the server.</summary>
public sealed record TaxGapsView(Guid Assessment, IReadOnlyList<TaxGapLineView> Lines, IReadOnlyList<TaxDeclarantGapView> Declarants)
{
    public static TaxGapsView Of(Guid assessment, TaxGapsResult g) => new(
        assessment,
        g.Lines.Select(l => new TaxGapLineView(l.Code, Amount(l.Notice), l.Nid is { } n ? Amount(n) : null, l.Gap is { } d ? Amount(d) : null)).ToList(),
        g.Declarants.Select(d => new TaxDeclarantGapView(d.Person, Amount(d.Notice), Amount(d.Nid), Amount(d.Gap))).ToList());
}

/// <summary>Issue #410: a line of « Ce qui a changé » (<see cref="TaxChangeLine"/>), amounts in euros; <c>shares</c> counts shares.</summary>
public sealed record TaxChangeLineView(string Code, Guid? Person, string Before, string Now, string Effect, bool FromNotice);

/// <summary>
/// Issue #410: <c>Year</c> against <c>Year − 1</c> (<see cref="TaxChangesResult"/>); no line and no <c>TaxBefore</c> when last year has
/// neither an estimate nor a notice.
/// </summary>
public sealed record TaxChangesView(
    int Year, string? TaxBefore, bool TaxBeforeFromNotice, string? TaxNow, string? Remainder, IReadOnlyList<TaxChangeLineView> Lines)
{
    public static TaxChangesView Of(int year, TaxChangesResult? r) => r is null
        ? new TaxChangesView(year, null, false, null, null, [])
        : new TaxChangesView(year, r.TaxBefore is { } b ? Amount(b) : null, r.TaxBeforeFromNotice, Amount(r.TaxNow),
            r.Remainder is { } rest ? Amount(rest) : null,
            r.Lines.Select(l => new TaxChangeLineView(l.Code, l.Person, Amount(l.Before), Amount(l.Now), Amount(l.Effect), l.FromNotice)).ToList());
}
