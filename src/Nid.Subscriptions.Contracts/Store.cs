using NodaTime;
using Nid.Core.Contracts;
using UnambitiousFx.Functional;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Subscriptions.Contracts;

/// <summary>
/// Where an imported row came from: its file id, the RFC 8785 hash of its file record, and who imported it.
/// <paramref name="ImportedBy"/> is null for a provider (its importer is <c>created_by</c>) and for rows written
/// before import provenance privacy (spec 2026-09-25 §3).
/// </summary>
public sealed record ImportRef(string Ref, string Hash, PersonId? ImportedBy = null);

/// <summary>What a row carries besides the engine's fields.</summary>
public sealed record RowOrigin(string? Note = null, ImportRef? Import = null)
{
    public static RowOrigin None { get; } = new();
}

public enum ImportedTable
{
    Subscription,
    BillingPhase,
    Purchase,
}

/// <summary>
/// An imported row: its id (the item id for a subscription), the hash it was imported with, who imported it
/// (null before import provenance privacy), and the subscription items it touches — what decides whether an
/// importer can see it.
/// </summary>
public sealed record ImportedRow(Guid Id, string Hash, PersonId? ImportedBy, IReadOnlyList<ItemId> Items);

/// <summary>A subscription as stored, for the export (1D2): what the file needs beyond the engine's input.</summary>
public sealed record StoredSubscription(
    ItemId Id, string? ProviderKey, bool AutoRenew, LocalDate? CancelledAt, string? Note, IReadOnlyList<PersonId> Users, ImportRef? Import,
    ContractTerms? Terms = null);

public sealed record StoredPhase(ItemId SubscriptionId, BillingPhase Phase, string? Note, ImportRef? Import);

public sealed record StoredPurchase(Purchase Purchase, string? Note, ImportRef? Import);

public sealed record StoredRows(IReadOnlyList<StoredSubscription> Subscriptions, IReadOnlyList<StoredPhase> Phases, IReadOnlyList<StoredPurchase> Purchases);

/// <summary>What a subscription needs beyond its Core item, for creation.</summary>
public sealed record NewSubscription(
    string? ProviderKey, bool AutoRenew, IReadOnlyList<PersonId> Users, string? Note,
    LocalDate? CancelledAt = null, ImportRef? Import = null, ContractTerms? Terms = null);

/// <summary>Fields of a subscription that can change without touching the Core item.</summary>
public sealed record SubscriptionEdit(
    bool? AutoRenew, LocalDate? CancelledAt, bool ClearCancelledAt, string? Note,
    string? ProviderKey = null, bool ClearProviderKey = false, ContractTermsChange? Terms = null);

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

/// <summary>1F3b: what an import's replacement sets on a subscription it created before; the rest of the row stays.</summary>
public sealed record SubscriptionReplacement(
    string? ProviderKey, bool AutoRenew, IReadOnlyList<PersonId> Users, string? Note, LocalDate? CancelledAt, ImportRef Import,
    ContractTerms? Terms = null);

/// <summary>
/// Rows in, engine input out. The store never computes: coverage and charges are the engine's job, and
/// nothing derived is persisted.
/// </summary>
public interface ISubscriptionStore
{
    Task<SubscriptionInput?> LoadAsync(ItemId id, CancellationToken ct);

    /// <summary>Loads several at once; ids the household does not hold are skipped, not faulted.</summary>
    Task<IReadOnlyList<SubscriptionInput>> LoadManyAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct);

    Task<Result> CreateAsync(ItemId id, NewSubscription subscription, CancellationToken ct);

    /// <summary>1F3b: an import's replacement of a subscription it created before. The row is updated in place.</summary>
    Task<Result> ReplaceAsync(ItemId id, SubscriptionReplacement replacement, CancellationToken ct);

    Task<Result> EditAsync(ItemId id, SubscriptionEdit edit, CancellationToken ct);

    /// <summary>The provider key of each of <paramref name="ids"/> that has one (1C1 wrote it and could not read it back).</summary>
    Task<IReadOnlyDictionary<ItemId, string>> ProviderKeysAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct);

    Task<Result<BillingPhaseId>> AddPhaseAsync(ItemId id, BillingPhase phase, RowOrigin origin, CancellationToken ct);

    /// <summary>
    /// Ends a phase on <paramref name="endsOn"/>. <paramref name="maxCycles"/>, when given, becomes its cycle limit: a price
    /// change leaves the closed phase the cycles it spent (1E2a final review).
    /// </summary>
    Task<Result> ClosePhaseAsync(ItemId id, BillingPhaseId phase, LocalDate endsOn, int? maxCycles, CancellationToken ct);

    Task<Result<PurchaseId>> AddPurchaseAsync(Purchase purchase, RowOrigin origin, CancellationToken ct);

    /// <summary>1F3b: an import's replacement of a phase it created before, updated in place (same id).</summary>
    Task<Result> ReplacePhaseAsync(ItemId id, BillingPhase phase, RowOrigin origin, CancellationToken ct);

    /// <summary>1F3b: an import's replacement of a purchase it created before, updated in place; its grants are reconciled.</summary>
    Task<Result> ReplacePurchaseAsync(Purchase purchase, RowOrigin origin, CancellationToken ct);

    /// <summary>
    /// Every row of <paramref name="table"/> imported under one of <paramref name="refs"/>, by ref, ordered by id,
    /// whoever imported it. The caller decides which of them the importer may see; nothing here filters.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<ImportedRow>>> FindImportedAsync(ImportedTable table, IReadOnlyCollection<string> refs, CancellationToken ct);

    /// <summary>
    /// The subscriptions among <paramref name="ids"/>, their phases, and every purchase granting to one of them with all
    /// of its grants (the caller drops the ones it may not show). Ids the store does not hold are skipped.
    /// </summary>
    Task<StoredRows> ReadAllAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct);

    /// <summary>
    /// The subscriptions a purchase grants coverage to — exactly what deleting it would take coverage away
    /// from. Empty when no such purchase exists, so a caller learns nothing about one it cannot reach.
    /// </summary>
    Task<IReadOnlyList<ItemId>> GrantedItemsAsync(PurchaseId purchase, CancellationToken ct);

    /// <summary>
    /// Deletes a purchase only if it actually grants to <paramref name="id"/> — a purchase belonging to a
    /// wholly different subscription is reported as not found, never as forbidden, so its existence is not
    /// disclosed to a caller who merely knows its id.
    /// </summary>
    Task<Result> DeletePurchaseAsync(ItemId id, PurchaseId purchase, CancellationToken ct);
}
