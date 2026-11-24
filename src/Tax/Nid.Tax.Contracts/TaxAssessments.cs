using Nid.Core.Contracts;

namespace Nid.Tax.Contracts;

/// <summary>Tax assessments spec §2: how a declarant's salaries were reduced, <c>allowance</c> or <c>actual</c> on the wire.</summary>
public enum SalaryDeduction { Allowance, Actual }

/// <summary>
/// Tax assessments spec §2: one declarant of a notice, <c>Rank</c> 1 or 2 (the notice's column). Every figure is nullable: absent
/// on the notice, absent here, never 0. <c>OvertimeExempt</c>, <c>Withholding</c> and <c>CreditsAdvance</c> are the notice's
/// detail lines (heures supplémentaires et jours RTT, RAS sur salaires et pensions, avance de 60 %).
/// </summary>
public sealed record TaxAssessmentDeclarant(
    PersonId Person, int Rank, decimal? GrossSalaries, SalaryDeduction? Deduction, decimal? DeductionAmount, decimal? NetSalaries,
    decimal? PerCeilingYear, decimal? PerUnusedOldest, decimal? PerUnusedMiddle, decimal? PerUnusedLatest,
    decimal? PerCeilingComputed, decimal? PerCeilingNext, decimal? OvertimeExempt = null, decimal? Withholding = null,
    decimal? CreditsAdvance = null);

/// <summary>
/// Tax assessments spec §2: a tax notice (avis d'imposition) of revenue year <c>Year</c>, issued in <c>IssuedYear</c>, in euros.
/// Rates are percents (0 to 100); <c>Balance</c> is signed (positive: to pay, negative: refunded). The figures after
/// <c>DocumentId</c> are the notice's detail lines (dividendes, RCM déjà soumis aux prélèvements sociaux, crédit d'impôt
/// prélèvement forfaitaire, déficits fonciers imputables, Pinel, frais de garde).
/// </summary>
public sealed record TaxAssessment(
    int Year, int IssuedYear, IReadOnlyList<TaxAssessmentDeclarant> Declarants, decimal? Shares, decimal? ReferenceIncome, decimal? AverageRate,
    decimal? MarginalRate, decimal? GrossIncome, decimal? TaxableIncome, decimal? CapitalIncomeDeclared, decimal? CapitalIncomeTaxable,
    decimal? RentalIncomeNet, decimal? FlatRateIncome, decimal? ExemptIncomeDeclared, decimal? ExemptIncomeNet, decimal? ScaleTax,
    decimal? ProportionalTax, decimal? TaxBeforeCredits, decimal? CreditsDeclared, decimal? CreditsRetained, decimal? NetTax,
    decimal? Withholding, decimal? Balance, decimal? RentalDeficitCarried, Guid? DocumentId, decimal? DividendsEligible = null,
    decimal? CapitalIncomeAlreadyLevied = null, decimal? FlatRatePrepaidCredit = null, decimal? RentalDeficitOnRentalIncome = null,
    decimal? RentalDeficitOnGlobalIncome = null, decimal? PinelReduction = null, decimal? ChildcareExpenses = null);

/// <summary>What the Documents module reads of the notices, never writes.</summary>
public interface ITaxAssessmentDeclarants
{
    /// <summary>Tax assessments spec §5: the declarants of a notice, for the link check. Null: no such notice.</summary>
    Task<IReadOnlySet<Guid>?> DeclarantsAsync(Guid assessmentId, CancellationToken ct);

    /// <summary>Issue #414: the years in which the notices <paramref name="declarant"/> declares were issued, those saved with their PDF only.</summary>
    Task<IReadOnlySet<int>> IssuedYearsWithDocumentAsync(PersonId declarant, CancellationToken ct);
}

/// <summary>Tax assessments spec §4: the codes of the notice's refusals.</summary>
public static class TaxAssessmentCodes
{
    public const string InvalidAssessment = "tax.invalid_assessment";
    public const string DeclarantTaken = "tax.declarant_taken";
    public const string DeclarantRequired = "tax.declarant_required";
    public const string Conflict = "tax.assessment_conflict";

    /// <summary>The store's answer to a write whose version is stale: the notice changed since it was read (answered as <see cref="Conflict"/>).</summary>
    public const string Changed = "tax.assessment_changed";

    /// <summary>The store's answer to the unique (year, person) index: a person already declares another notice of that year (answered as <see cref="DeclarantTaken"/>).</summary>
    public const string Taken = "tax.assessment_taken";
}

/// <summary>
/// #516: aimed at one stored notice. The tax assessment behavior answers « not found » (« tax assessment ») for one the actor does not
/// declare, whatever the access (tax assessments spec §4: its declarants read and change it, nobody else sees it), and hands the notice
/// to the handler.
/// </summary>
public interface ITaxAssessmentScoped
{
    Guid TaxAssessmentId { get; }
}
