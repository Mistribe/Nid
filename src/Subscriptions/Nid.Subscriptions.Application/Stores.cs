using NodaTime;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using UnambitiousFx.Functional;

namespace Nid.Subscriptions.Application;

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
    Insurance,
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

/// <summary>The insurance details of a subscription as stored, with where they were imported from.</summary>
public sealed record StoredInsurance(ItemId SubscriptionId, InsuranceDetails Details, ImportRef? Import);

public sealed record StoredRows(IReadOnlyList<StoredSubscription> Subscriptions, IReadOnlyList<StoredPhase> Phases, IReadOnlyList<StoredPurchase> Purchases);

/// <summary>What a subscription needs beyond its Core item, for creation.</summary>
public sealed record NewSubscription(
    string? ProviderKey, bool AutoRenew, IReadOnlyList<PersonId> Users, string? Note,
    LocalDate? CancelledAt = null, ImportRef? Import = null, ContractTerms? Terms = null);

/// <summary>Fields of a subscription that can change without touching the Core item.</summary>
public sealed record SubscriptionEdit(
    bool? AutoRenew, LocalDate? CancelledAt, bool ClearCancelledAt, string? Note,
    string? ProviderKey = null, bool ClearProviderKey = false, ContractTermsChange? Terms = null);

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

    /// <summary>#628: serialises the phase writes of one subscription until the transaction ends.</summary>
    Task LockAsync(ItemId id, CancellationToken ct);

    /// <summary>
    /// Ends a phase on <paramref name="endsOn"/>. <paramref name="maxCycles"/>, when given, becomes its cycle limit: a price
    /// change leaves the closed phase the cycles it spent (1E2a final review), and no limit when it spent none (#628).
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

    /// <summary>The insurance details of a subscription, or null when it has none.</summary>
    Task<InsuranceDetails?> FindInsuranceAsync(ItemId id, CancellationToken ct);

    /// <summary>The insurance details of each of <paramref name="ids"/> that has some, for the export.</summary>
    Task<IReadOnlyDictionary<ItemId, StoredInsurance>> ReadInsurancesAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct);

    /// <summary>
    /// Sets the insurance details, guarantees replaced whole (positions from the list order). Not found when the
    /// subscription does not exist. <paramref name="import"/>, when given, is the row's import provenance.
    /// </summary>
    Task<Result> SetInsuranceAsync(ItemId id, InsuranceDetails details, ImportRef? import, CancellationToken ct);

    Task<Result> DeleteInsuranceAsync(ItemId id, CancellationToken ct);
}

public enum CatalogRunStatus
{
    Running,
    Succeeded,
    Failed,
}

/// <summary>One row of <c>subscriptions.catalog_sync_run</c> (design §3.6). <paramref name="Report"/> is null until a sync succeeded.</summary>
public sealed record CatalogSyncRun(
    Guid Id,
    Instant StartedAt,
    Instant? FinishedAt,
    CatalogRunStatus Status,
    string Commit,
    string? FailureCode,
    string? FailureMessage,
    CatalogReport? Report)
{
    /// <summary>
    /// Host-only (spec §3.6, "Judged as of the sync"): for every adopted-replaced, adopted-remaining and conflict key of
    /// <see cref="Report"/>, the subscriptions that used it when the sync ran. It decides what a reader sees of those
    /// entries and is never serialized to a person: the views carry the filtered report, not this.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ItemId>> UsedBy { get; init; } = NoUsage;

    public static IReadOnlyDictionary<string, IReadOnlyList<ItemId>> NoUsage { get; } =
        new Dictionary<string, IReadOnlyList<ItemId>>(StringComparer.Ordinal);
}

/// <summary>The sync journal: the same shape and rules as <c>core.fx_sync_run</c> (design §3.6, 1C2a design §10).</summary>
public interface ICatalogRunLog
{
    /// <summary>
    /// Starts a run of <paramref name="commit"/>, or returns <c>null</c> when one is already running: the database
    /// holds at most one running row. A run still running an hour after it started is first closed as
    /// <see cref="CatalogCodes.RunAbandoned"/>, so a crash never blocks the sync for ever.
    /// </summary>
    Task<CatalogSyncRun?> TryStartAsync(string commit, CancellationToken ct);

    Task FinishAsync(CatalogSyncRun run, CancellationToken ct);

    /// <summary>
    /// Whether a run is under way. A run still running an hour after it started is closed as
    /// <see cref="CatalogCodes.RunAbandoned"/> first and does not count: nothing else would close it once a sync
    /// has succeeded, the catalog having no schedule (final review F1).
    /// </summary>
    Task<bool> IsRunningAsync(CancellationToken ct);

    /// <summary>Closes as <see cref="CatalogCodes.RunAbandoned"/> every run still running an hour after it started.</summary>
    Task CloseAbandonedAsync(CancellationToken ct);

    /// <summary>Whether a sync has ever succeeded: the startup trigger's condition (design §4 "Trigger").</summary>
    Task<bool> HasSucceededAsync(CancellationToken ct);

    /// <summary>The most recent runs first.</summary>
    Task<IReadOnlyList<CatalogSyncRun>> RecentAsync(int count, CancellationToken ct);

    /// <summary>One page of runs, newest first, and the total number of runs.</summary>
    Task<(IReadOnlyList<CatalogSyncRun> Items, int Total)> PageAsync(int skip, int take, CancellationToken ct);

    /// <summary>Deletes finished runs started before <paramref name="before"/>, except the latest succeeded one.</summary>
    Task<int> PurgeBeforeAsync(Instant before, CancellationToken ct);
}

/// <summary>Providers and labels (design §3). Visibility is not decided here: see <c>ProviderAccess</c>.</summary>
public interface IProviderStore
{
    /// <summary>Every label, ordered by key.</summary>
    Task<IReadOnlyList<Label>> LabelsAsync(CancellationToken ct);

    /// <summary>
    /// The providers among <paramref name="keys"/>, whoever may see them: for a caller that already holds a visible
    /// subscription naming them (a view) or has no person to check (the system actor).
    /// </summary>
    Task<IReadOnlyDictionary<string, Provider>> FindAsync(IReadOnlyCollection<string> keys, CancellationToken ct);

    /// <summary>
    /// Design §3.5 (amended): every catalog provider; a custom or adopted one used by a subscription among
    /// <paramref name="visibleItems"/>; a custom one <paramref name="actor"/> created while no subscription among
    /// <paramref name="visibleItems"/> uses it. Archived providers included (the list says so), ordered by name then key.
    /// <paramref name="nameContains"/> filters by name, ignoring case, taken literally; blank means no filter.
    /// </summary>
    Task<IReadOnlyList<Provider>> VisibleAsync(PersonId actor, IReadOnlyCollection<ItemId> visibleItems, string? nameContains, CancellationToken ct);

    /// <summary>The providers among <paramref name="keys"/> the same rule lets <paramref name="actor"/> see.</summary>
    Task<IReadOnlyDictionary<string, Provider>> FindVisibleAsync(
        PersonId actor, IReadOnlyCollection<ItemId> visibleItems, IReadOnlyCollection<string> keys, CancellationToken ct);

    /// <summary>Whether any provider, of any origin, holds <paramref name="key"/>.</summary>
    Task<bool> ExistsAsync(string key, CancellationToken ct);

    /// <summary>Every provider imported under one of <paramref name="refs"/>, whoever imported it (the caller decides which it may use).</summary>
    Task<IReadOnlyList<Provider>> FindImportedAsync(IReadOnlyCollection<string> refs, CancellationToken ct);

    /// <summary>Inserts a provider; <see cref="CatalogCodes.KeyTaken"/> when its key exists already.</summary>
    Task<Result> CreateAsync(Provider provider, CancellationToken ct);

    /// <summary>
    /// For each of <paramref name="keys"/>, the subscriptions using it now (an empty list when none), whoever may see
    /// them: what a sync records, host-only, to judge its report for each reader later (spec §3.6).
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<ItemId>>> UsageAsync(IReadOnlyCollection<string> keys, CancellationToken ct);

    /// <summary>
    /// Inside the caller's transaction, locks <c>provider</c> and <c>label</c> against every other writer (an import
    /// committing a custom provider waits, and is waited for) and reads them. Throws outside a transaction.
    /// </summary>
    Task<CatalogSnapshot> LockForSyncAsync(CancellationToken ct);

    /// <summary>Writes <paramref name="plan"/> in the caller's transaction; archived providers get <paramref name="now"/>.</summary>
    Task WriteCatalogAsync(CatalogPlan plan, Instant now, CancellationToken ct);

    /// <summary>
    /// The live (not archived) catalog providers whose catalog declares an icon, in key order (ordinal): what the icon
    /// pass looks at (1C2c design §5).
    /// </summary>
    Task<IReadOnlyList<Provider>> CatalogIconsAsync(CancellationToken ct);

    /// <summary>
    /// Links the stored icon <paramref name="asset"/>, fetched from <paramref name="source"/>, to the catalog provider
    /// <paramref name="key"/>: one statement, its own transaction. Answers how many rows it updated: 0 when the
    /// provider's icon URL is no longer <paramref name="source"/> (a sync changed it meanwhile) — nothing was linked.
    /// </summary>
    Task<int> SetIconAsync(string key, string source, string asset, CancellationToken ct);

    /// <summary>Whether <paramref name="hash"/> is the stored icon of a catalog provider, archived or not (1C2c design §6).</summary>
    Task<bool> IsCatalogIconAsync(string hash, CancellationToken ct);

    /// <summary>1F3b: an import's replacement of a custom provider it created before; key, origin and creator stay.</summary>
    Task<Result> ReplaceImportedAsync(string key, string name, string? website, string? pricingPage,
        IReadOnlyList<string> labelKeys, ImportRef import, CancellationToken ct);
}

/// <summary>
/// One catalog icon, downloaded and processed (1C2c design §4, §5). A failure's code is an <see cref="IconCodes"/>
/// reason; a cancellation of <paramref name="ct"/> is thrown, never reported as a refusal.
/// </summary>
public interface ICatalogIconSource
{
    Task<Result<ProcessedIcon>> FetchAsync(string url, CancellationToken ct);
}

/// <summary>The two files of one catalog version, as fetched.</summary>
public sealed record CatalogFiles(byte[] Labels, byte[] Providers);

/// <summary>The catalog source (design §4 "Source"). Failures carry <see cref="CatalogCodes"/> source codes; nothing throws.</summary>
public interface ICatalogSourceClient
{
    Task<Result<CatalogFiles>> FetchAsync(string commit, CancellationToken ct);
}

/// <summary>Starts the <c>catalog-sync</c> job now (Quartz in the host); <see cref="CatalogCodes.JobsDisabled"/> when jobs are off.</summary>
public interface ICatalogJobTrigger
{
    Task<Result> TriggerSyncAsync(CancellationToken ct);
}

/// <summary>An icon as Nid stores it: a lossless WebP within 256 px, without metadata; <paramref name="Hash"/> is the SHA-256 of <paramref name="Webp"/>.</summary>
public sealed record ProcessedIcon(byte[] Webp, string Hash, int Width, int Height);
