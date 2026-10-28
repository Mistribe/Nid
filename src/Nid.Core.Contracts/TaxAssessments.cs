namespace Nid.Core.Contracts;

/// <summary>Tax assessments spec §2: how a declarant's salaries were reduced, <c>allowance</c> or <c>actual</c> on the wire.</summary>
public enum SalaryDeduction { Allowance, Actual }

/// <summary>
/// Tax assessments spec §2: one declarant of a notice, <c>Rank</c> 1 or 2 (the notice's column). Every figure is nullable: absent
/// on the notice, absent here, never 0.
/// </summary>
public sealed record TaxAssessmentDeclarant(
    PersonId Person, int Rank, decimal? GrossSalaries, SalaryDeduction? Deduction, decimal? DeductionAmount, decimal? NetSalaries,
    decimal? PerCeilingYear, decimal? PerUnusedOldest, decimal? PerUnusedMiddle, decimal? PerUnusedLatest,
    decimal? PerCeilingComputed, decimal? PerCeilingNext);

/// <summary>
/// Tax assessments spec §2: a tax notice (avis d'imposition) of revenue year <c>Year</c>, issued in <c>IssuedYear</c>, in euros.
/// Rates are percents (0 to 100); <c>Balance</c> is signed (positive: to pay, negative: refunded).
/// </summary>
public sealed record TaxAssessment(
    int Year, int IssuedYear, IReadOnlyList<TaxAssessmentDeclarant> Declarants, decimal? Shares, decimal? ReferenceIncome, decimal? AverageRate,
    decimal? MarginalRate, decimal? GrossIncome, decimal? TaxableIncome, decimal? CapitalIncomeDeclared, decimal? CapitalIncomeTaxable,
    decimal? RentalIncomeNet, decimal? FlatRateIncome, decimal? ExemptIncomeDeclared, decimal? ExemptIncomeNet, decimal? ScaleTax,
    decimal? ProportionalTax, decimal? TaxBeforeCredits, decimal? CreditsDeclared, decimal? CreditsRetained, decimal? NetTax,
    decimal? Withholding, decimal? Balance, decimal? RentalDeficitCarried, Guid? DocumentId);

/// <summary>A stored notice: its id and its version (optimistic concurrency).</summary>
public sealed record StoredTaxAssessment(Guid Id, uint Version, TaxAssessment Assessment);

public interface ITaxAssessmentStore
{
    /// <summary>The notices where <paramref name="reader"/> is a declarant, optionally of one <paramref name="year"/>.</summary>
    Task<IReadOnlyList<StoredTaxAssessment>> ListForAsync(PersonId reader, int? year, CancellationToken ct);

    Task<StoredTaxAssessment?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>Whether one of <paramref name="persons"/> is already a declarant of a notice of <paramref name="year"/>, other than <paramref name="except"/>.</summary>
    Task<bool> PersonTakenAsync(int year, IReadOnlyCollection<PersonId> persons, Guid? except, CancellationToken ct);

    Task<Guid> AddAsync(TaxAssessment a, CancellationToken ct);

    /// <summary>Writes <paramref name="a"/> if the stored version is still <paramref name="version"/>; <see cref="TaxAssessmentChangedException"/> otherwise.</summary>
    Task UpdateAsync(Guid id, TaxAssessment a, uint version, CancellationToken ct);

    Task DeleteAsync(Guid id, CancellationToken ct);
}

/// <summary>The store's answer to a write whose version is stale: the notice changed since it was read.</summary>
public sealed class TaxAssessmentChangedException : Exception;

/// <summary>The store's answer to the unique (year, person) index: a person already declares another notice of that year.</summary>
public sealed class TaxAssessmentTakenException : Exception;

/// <summary>Tax assessments spec §5: the declarants of a notice, for the Documents module's link check. Null: no such notice.</summary>
public interface ITaxAssessmentDeclarants
{
    Task<IReadOnlySet<Guid>?> DeclarantsAsync(Guid assessmentId, CancellationToken ct);
}

/// <summary>Tax assessments spec §4: the codes of the notice's refusals.</summary>
public static class TaxAssessmentCodes
{
    public const string InvalidAssessment = "tax.invalid_assessment";
    public const string DeclarantTaken = "tax.declarant_taken";
    public const string DeclarantRequired = "tax.declarant_required";
    public const string Conflict = "tax.assessment_conflict";
}
