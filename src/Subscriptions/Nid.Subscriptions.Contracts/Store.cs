using NodaTime;
using Nid.Core.Contracts;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Subscriptions.Contracts;

/// <summary>
/// Where an imported row came from: its file id, the RFC 8785 hash of its file record, and who imported it.
/// <paramref name="ImportedBy"/> is null for a provider (its importer is <c>created_by</c>) and for rows written
/// before import provenance privacy (spec 2026-09-25 §3).
/// </summary>
public sealed record ImportRef(string Ref, string Hash, PersonId? ImportedBy = null);

/// <summary>
/// A PATCH of the contract terms: a field is set by its value, cleared by its flag, or kept when neither is given.
/// </summary>
public sealed record ContractTermsChange(
    LocalDate? AnniversaryOn, bool ClearAnniversaryOn, Duration? Notice, bool ClearNotice,
    LocalDate? CommitmentEndsOn, bool ClearCommitmentEndsOn, LocalDate? CancellableFrom, bool ClearCancellableFrom)
{
    /// <summary>The terms once this change is applied to <paramref name="current"/> (null reads as no terms).</summary>
    public ContractTerms ApplyTo(ContractTerms? current)
        => new(
            ClearAnniversaryOn ? null : AnniversaryOn ?? current?.AnniversaryOn,
            ClearNotice ? null : Notice ?? current?.Notice,
            ClearCommitmentEndsOn ? null : CommitmentEndsOn ?? current?.CommitmentEndsOn,
            ClearCancellableFrom ? null : CancellableFrom ?? current?.CancellableFrom);
}
