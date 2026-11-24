using Nid.Core.Contracts;
using Nid.Tax.Contracts;
using UnambitiousFx.Functional;

namespace Nid.Tax.Application;

/// <summary>A stored profile, the revenue year it was saved for (an earlier one when the year has none yet) and its version (<c>xmin</c>).</summary>
public sealed record StoredTaxProfile(int Year, TaxProfile Profile, uint Version = 0);

public interface ITaxProfileStore
{
    /// <summary>The profile of <paramref name="year"/>, or of the latest year before it; null when none is stored.</summary>
    Task<StoredTaxProfile?> FindAsync(int year, CancellationToken ct);

    /// <summary>
    /// #630: false when the year's row is no longer at <paramref name="version"/> — null is « the year had no profile of its own », so a row
    /// saved meanwhile is a conflict too.
    /// </summary>
    Task<bool> SaveAsync(int year, TaxProfile profile, uint? version, CancellationToken ct);
}

/// <summary>A stored notice: its id and its version (optimistic concurrency).</summary>
public sealed record StoredTaxAssessment(Guid Id, uint Version, TaxAssessment Assessment);

public interface ITaxAssessmentStore
{
    /// <summary>The notices where <paramref name="reader"/> is a declarant, optionally of one <paramref name="year"/>.</summary>
    Task<IReadOnlyList<StoredTaxAssessment>> ListForAsync(PersonId reader, int? year, CancellationToken ct);

    Task<StoredTaxAssessment?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>Whether one of <paramref name="persons"/> is already a declarant of a notice of <paramref name="year"/>, other than <paramref name="except"/>.</summary>
    Task<bool> PersonTakenAsync(int year, IReadOnlyCollection<PersonId> persons, Guid? except, CancellationToken ct);

    /// <summary>Whether <paramref name="person"/> declares any notice.</summary>
    Task<bool> DeclaresAsync(PersonId person, CancellationToken ct);

    /// <summary>The new notice's id; <see cref="TaxAssessmentCodes.Taken"/> when the unique (year, person) index refuses it.</summary>
    Task<Result<Guid>> AddAsync(TaxAssessment a, CancellationToken ct);

    /// <summary>
    /// Writes <paramref name="a"/> if the stored version is still <paramref name="version"/>: <see cref="TaxAssessmentCodes.Changed"/>
    /// otherwise, <see cref="TaxAssessmentCodes.Taken"/> when the unique (year, person) index refuses it.
    /// </summary>
    Task<Result> UpdateAsync(Guid id, TaxAssessment a, uint version, CancellationToken ct);

    Task DeleteAsync(Guid id, CancellationToken ct);
}
