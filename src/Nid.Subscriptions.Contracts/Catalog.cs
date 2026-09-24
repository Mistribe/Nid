using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Subscriptions.Contracts;

/// <summary>Where a provider comes from (design §3.2): the catalog, a person, or a key stored before any catalog (§3.4).</summary>
public enum ProviderOrigin
{
    Catalog,
    Custom,
    Adopted,
}

public enum LabelOrigin
{
    Catalog,
    Local,
}

public static class CatalogNames
{
    public static string ToWire(this ProviderOrigin origin) => origin switch
    {
        ProviderOrigin.Catalog => "catalog",
        ProviderOrigin.Custom => "custom",
        ProviderOrigin.Adopted => "adopted",
        _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };

    public static ProviderOrigin ProviderOriginFromWire(string origin) => origin switch
    {
        "catalog" => ProviderOrigin.Catalog,
        "custom" => ProviderOrigin.Custom,
        "adopted" => ProviderOrigin.Adopted,
        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown provider origin."),
    };

    public static string ToWire(this LabelOrigin origin) => origin == LabelOrigin.Local ? "local" : "catalog";

    public static LabelOrigin LabelOriginFromWire(string origin) => origin switch
    {
        "catalog" => LabelOrigin.Catalog,
        "local" => LabelOrigin.Local,
        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown label origin."),
    };
}

/// <summary>The binding values of the design (§3, §4, §5), written once.</summary>
public static class CatalogLimits
{
    public const int MaxKeyLength = 64;
    public const int MaxNameLength = 100;
    public const int MaxLabelNameLength = 50;
    public const int MaxUrlLength = 2048;

    /// <summary>Per file of the catalog (design §4 "Source").</summary>
    public const int MaxFileBytes = 1024 * 1024;

    /// <summary>What <c>GET /catalog/runs</c> answers (design §5).</summary>
    public const int RecentRuns = 50;

    public const int RunRetentionDays = 400;

    /// <summary>The one local label, created by the migration for adopted providers (design §3.1).</summary>
    public const string OtherLabel = "other";

    /// <summary>The colour of <see cref="OtherLabel"/> and of a label created for a category without one (design §4).</summary>
    public const string NeutralColor = "#9E9E9E";
}

public static class CatalogCodes
{
    public const string UnknownProvider = "subscriptions.unknown_provider";
    public const string InvalidProvider = "subscriptions.invalid_provider";
    public const string SyncRunning = "subscriptions.catalog_sync_running";
    public const string SourceUnavailable = "subscriptions.catalog_source_unavailable";
    public const string SourceRefused = "subscriptions.catalog_source_refused";
    public const string SourceMalformed = "subscriptions.catalog_source_malformed";
    public const string RunAbandoned = "subscriptions.catalog_run_abandoned";
    public const string RunInterrupted = "subscriptions.catalog_run_interrupted";
    public const string JobsDisabled = "subscriptions.catalog_jobs_disabled";

    /// <summary>A generated key lost a race with another insert: the caller generates another one (design §3.3).</summary>
    public const string KeyTaken = "subscriptions.provider_key_taken";

    /// <summary>Reasons of a <see cref="CatalogRefusal"/>: data, not failures.</summary>
    public const string InvalidKey = "invalid_key";
    public const string InvalidName = "invalid_name";
    public const string InvalidUrl = "invalid_url";
    public const string NoCategory = "no_category";
    public const string InvalidCategory = "invalid_category";
    public const string InvalidColor = "invalid_color";
    public const string NotAnObject = "not_an_object";
    public const string ReservedKey = "reserved_key";
}

/// <summary>A label (design §3.1). The UI translates labels by key; <paramref name="Color"/> is CSS <c>#RRGGBB</c>.</summary>
public sealed record Label(string Key, string Name, string Color, LabelOrigin Origin);

/// <summary>A provider (design §3.2). <paramref name="Import"/> is the provenance of one created by an import (§5).</summary>
public sealed record Provider(
    string Key,
    ProviderOrigin Origin,
    string Name,
    string? Website,
    string? PricingPage,
    IReadOnlyList<string> LabelKeys,
    string? CatalogCommit,
    PersonId? CreatedBy,
    Instant? ArchivedAt,
    ImportRef? Import)
{
    public bool IsArchived => ArchivedAt is not null;

    // The compiler-generated equality would compare LabelKeys by reference: two providers read back from storage
    // would never equal one another. Compare it by content instead (as FxSyncRun does in 1C2a).
    public bool Equals(Provider? other)
        => other is not null
           && Key == other.Key && Origin == other.Origin && Name == other.Name
           && Website == other.Website && PricingPage == other.PricingPage
           && LabelKeys.SequenceEqual(other.LabelKeys)
           && CatalogCommit == other.CatalogCommit && CreatedBy == other.CreatedBy
           && ArchivedAt == other.ArchivedAt && Import == other.Import;

    public override int GetHashCode()
        => HashCode.Combine(Key, Origin, Name, Website, PricingPage, CatalogCommit, CreatedBy, ArchivedAt);
}

/// <summary>An entry of a catalog file that was not read, with why (design §4). <paramref name="Index"/> is its position in its file.</summary>
public sealed record CatalogRefusal(string File, int Index, string? Key, string Reason);

/// <summary>A key a catalog file holds twice: the first occurrence wins (design §4).</summary>
public sealed record CatalogDuplicate(string File, string Key);

/// <summary>What one sync did (design §3.6). Every list is sorted by key, ordinal.</summary>
public sealed record CatalogReport(
    IReadOnlyList<string> ProvidersAdded,
    IReadOnlyList<string> ProvidersUpdated,
    IReadOnlyList<string> ProvidersUnchanged,
    IReadOnlyList<string> ProvidersArchived,
    IReadOnlyList<string> AdoptedReplaced,
    IReadOnlyList<string> AdoptedRemaining,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> LabelsAdded,
    IReadOnlyList<string> LabelsUpdated,
    IReadOnlyList<string> LabelsCreated,
    IReadOnlyList<CatalogDuplicate> Duplicates,
    IReadOnlyList<CatalogRefusal> Refused)
{
    public static CatalogReport Empty { get; } = new([], [], [], [], [], [], [], [], [], [], [], []);
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
}

/// <summary>A label as the catalog gives it, already checked; <paramref name="Color"/> is CSS <c>#RRGGBB</c>.</summary>
public sealed record CatalogLabel(string Key, string Name, string Color);

/// <summary>A provider as the catalog gives it, already checked; <paramref name="LabelKeys"/> are its <c>categories</c>.</summary>
public sealed record CatalogProvider(string Key, string Name, string? Website, string? PricingPage, IReadOnlyList<string> LabelKeys);

/// <summary>
/// What the readers make of the two files (design §4). <paramref name="Labels"/> includes the labels created for a
/// category without one, whose keys <paramref name="LabelsCreated"/> lists; what was not read is in <paramref name="Refused"/>.
/// </summary>
public sealed record CatalogReading(
    IReadOnlyList<CatalogLabel> Labels,
    IReadOnlyList<CatalogProvider> Providers,
    IReadOnlyList<string> LabelsCreated,
    IReadOnlyList<CatalogDuplicate> Duplicates,
    IReadOnlyList<CatalogRefusal> Refused);

/// <summary>What is stored, read under the sync's lock (design §4 "Merge").</summary>
public sealed record CatalogSnapshot(IReadOnlyList<Provider> Providers, IReadOnlyList<Label> Labels);

/// <summary>
/// What one sync writes: labels and providers as they must be stored (inserted or updated), catalog keys to archive,
/// and the report. A custom provider and a local label never appear in it.
/// </summary>
public sealed record CatalogPlan(IReadOnlyList<Label> Labels, IReadOnlyList<Provider> Providers, IReadOnlyList<string> Archive, CatalogReport Report);

/// <summary>The two files of one catalog version, as fetched.</summary>
public sealed record CatalogFiles(byte[] Labels, byte[] Providers);

/// <summary>The catalog source (design §4 "Source"). Failures carry <see cref="CatalogCodes"/> source codes; nothing throws.</summary>
public interface ICatalogSourceClient
{
    Task<Result<CatalogFiles>> FetchAsync(string commit, CancellationToken ct);
}

/// <summary>Bound from the <c>Catalog</c> configuration section by the host; the defaults are the design's (§4).</summary>
public sealed class CatalogOptions
{
    /// <summary>A file is <c>{BaseUrl}/{Commit}/providers.json</c>; <c>raw.githubusercontent.com</c> is already an allowed host.</summary>
    public string BaseUrl { get; set; } = "https://raw.githubusercontent.com/Mistribe/SubTracker-Data";

    /// <summary>2025-11-29, the latest commit touching <c>providers.json</c>. Changing version is changing this and syncing again.</summary>
    public string Commit { get; set; } = "df0146a8f3532f5c790aa54a34d4871e96b62a95";
}

/// <summary>Starts the <c>catalog-sync</c> job now (Quartz in the host); <see cref="CatalogCodes.JobsDisabled"/> when jobs are off.</summary>
public interface ICatalogJobTrigger
{
    Task<Result> TriggerSyncAsync(CancellationToken ct);
}
