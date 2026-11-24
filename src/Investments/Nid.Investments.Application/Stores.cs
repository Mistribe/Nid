using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Investments.Application;

/// <summary>
/// Issue #209: the household's fee references, in %: contract fees by envelope kind, support fees by holding kind (wire values), and
/// the assumed gross return their cost is projected at.
/// </summary>
public sealed record FeeReferences(decimal AssumedReturnPct, IReadOnlyDictionary<string, decimal> Envelopes, IReadOnlyDictionary<string, decimal> Holdings);

/// <summary>#513: the settings Investments keeps in its own schema, moved from <c>core.household_settings</c>.</summary>
public interface IInvestmentsSettings
{
    /// <summary>Issue #209: the fee references the household saved; null until it saved some (<c>FeesProjection</c> holds the defaults).</summary>
    Task<FeeReferences?> FeeReferencesAsync(CancellationToken ct);

    /// <summary>Replaces the fee references; checked by the caller.</summary>
    Task SaveFeeReferencesAsync(FeeReferences references, CancellationToken ct);
}

public sealed record ImportedEnvelope(ItemId Item, string Hash, PersonId ImportedBy);
public sealed record StoredEnvelope(Envelope Envelope, ImportMark? Import);

/// <summary>A change: a null field is left as it is; <c>Clear*</c> empties an optional one.</summary>
public sealed record EnvelopeChange(
    EnvelopeKind? Kind = null, string? Institution = null, bool ClearInstitution = false,
    string? AccountHint = null, bool ClearAccountHint = false, LocalDate? OpenedOn = null, bool ClearOpenedOn = false,
    ValuationCadence? Cadence = null, int? StaleAfterDays = null, bool ClearStaleAfterDays = false,
    string? Note = null, bool ClearNote = false, decimal? UcShareFloorPct = null, bool ClearUcShareFloorPct = false,
    decimal? PremiumsBefore2017 = null, bool ClearPremiumsBefore2017 = false, RegulatedKind? Regulated = null, bool ClearRegulated = false,
    IReadOnlyList<AvailabilityTranche>? Availability = null, bool ClearAvailability = false,
    EarlyRelease? EarlyRelease = null, bool ClearEarlyRelease = false);

/// <summary>The envelopes' table; its reads by id are <see cref="IEnvelopes"/>, what other modules see of it.</summary>
public interface IEnvelopeStore : IEnvelopes
{
    Task CreateAsync(Envelope envelope, CancellationToken ct);

    Task CreateAsync(Envelope envelope, ImportMark? import, CancellationToken ct);

    /// <summary>2B1b: an import's replacement: every attribute, the closing date and the mark, in place.</summary>
    Task ReplaceAsync(Envelope envelope, ImportMark import, CancellationToken ct);

    Task SetClosedOnAsync(ItemId item, LocalDate? closedOn, CancellationToken ct);

    Task<IReadOnlyDictionary<string, IReadOnlyList<ImportedEnvelope>>> FindImportedAsync(IReadOnlyCollection<string> refs, CancellationToken ct);

    Task<IReadOnlyList<StoredEnvelope>> ReadAllAsync(IReadOnlyCollection<ItemId> items, CancellationToken ct);

    Task UpdateAsync(ItemId item, EnvelopeChange change, CancellationToken ct);
}

public sealed record StoredHolding(EnvelopeHolding Holding, uint Version);

/// <summary>A holding with a maturity date, for the upcoming events.</summary>
public sealed record MaturingHolding(ItemId Envelope, Guid HoldingId, string Name, LocalDate MaturesOn);

/// <summary>Issue #180: an open SCPI with an entitlement delay, the days it bought shares (its allocations in) and the day it was opened.</summary>
public sealed record ScpiHolding(ItemId Envelope, Guid HoldingId, string Name, int DelayMonths, LocalDate? OpenedOn, IReadOnlyList<LocalDate> Bought);

/// <summary>import-format.md §8: the holdings tables an import marks with the file id a row came from.</summary>
public enum HoldingTable { Holding, Snapshot, Movement }

/// <summary>A row imported under a file id: its id, its envelope, the entity's hash, and who imported it.</summary>
public sealed record ImportedHoldingRow(Guid Id, ItemId Envelope, string Hash, PersonId ImportedBy);

/// <summary>What the export writes of some envelopes: their holdings, snapshots and movements, and the marks of those imported.</summary>
public sealed record HoldingsExport(
    IReadOnlyList<EnvelopeHolding> Holdings, IReadOnlyList<HoldingSnapshot> Snapshots, IReadOnlyList<HoldingMovement> Movements,
    IReadOnlyDictionary<Guid, ImportMark> Marks);

public interface IHoldingStore
{
    /// <summary>
    /// The distinct ISINs to price: holdings with an ISIN and <c>auto_price</c>, neither <c>euro_fund</c> nor <c>cash</c>, not closed,
    /// in envelopes that are not closed.
    /// </summary>
    Task<IReadOnlyList<string>> PricedIsinsAsync(CancellationToken ct);

    /// <summary>Per ISIN, the unit price of its latest snapshot line that has a positive one, with its envelope.</summary>
    Task<IReadOnlyList<SnapshotPrice>> SnapshotUnitPricesAsync(IReadOnlyCollection<string> isins, CancellationToken ct);

    Task<IReadOnlyList<StoredHolding>> HoldingsAsync(ItemId envelope, CancellationToken ct);

    /// <summary>
    /// The holdings of these envelopes that have units (<see cref="HoldingWire.HasUnits"/>), closed ones included, whose name contains
    /// <paramref name="nameContains"/> (typed text, case-insensitive; <c>%</c> and <c>_</c> match themselves), every one when null; by name.
    /// A registry-free read: the caller passes the envelopes the reader can see.
    /// </summary>
    Task<IReadOnlyList<EnvelopeHolding>> PricedAsync(IReadOnlyCollection<ItemId> envelopes, string? nameContains, CancellationToken ct);

    Task AddAsync(EnvelopeHolding holding, CancellationToken ct);

    /// <summary>Several holdings in one save.</summary>
    Task AddManyAsync(IReadOnlyList<EnvelopeHolding> holdings, CancellationToken ct);

    /// <summary>Every field of <paramref name="holding"/>, in place; false when the row is no longer at <paramref name="version"/> (null: unchecked, the import).</summary>
    Task<bool> ReplaceAsync(EnvelopeHolding holding, uint? version, CancellationToken ct);

    /// <summary>False when the row is no longer at <paramref name="version"/>.</summary>
    Task<bool> DeleteAsync(Guid holding, uint version, CancellationToken ct);

    /// <summary>True when a snapshot line or a movement names the holding.</summary>
    Task<bool> InUseAsync(Guid holding, CancellationToken ct);

    /// <summary>The envelope's snapshots, latest first.</summary>
    Task<IReadOnlyList<HoldingSnapshot>> SnapshotsAsync(ItemId envelope, CancellationToken ct);

    /// <summary>Creates the snapshot, or replaces the one of that id, lines included.</summary>
    Task SaveSnapshotAsync(HoldingSnapshot snapshot, CancellationToken ct);

    Task DeleteSnapshotAsync(Guid snapshot, CancellationToken ct);

    /// <summary>The envelope's movements, by date then id.</summary>
    Task<IReadOnlyList<HoldingMovement>> MovementsAsync(ItemId envelope, CancellationToken ct);

    Task AddMovementsAsync(IReadOnlyList<HoldingMovement> movements, CancellationToken ct);

    /// <summary>An import's replacement: every field of the movement, in place.</summary>
    Task ReplaceMovementAsync(HoldingMovement movement, CancellationToken ct);

    /// <summary>Every importer's rows of <paramref name="table"/> under these file ids: the import section decides which one the importer may see.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<ImportedHoldingRow>>> FindImportedAsync(HoldingTable table, IReadOnlyCollection<string> refs, CancellationToken ct);

    /// <summary>The rows of these envelopes, in as many queries whatever their number.</summary>
    Task<HoldingsExport> ExportAsync(IReadOnlyCollection<ItemId> envelopes, CancellationToken ct);

    /// <summary>Marks a row just written, in the import's transaction.</summary>
    Task MarkAsync(HoldingTable table, Guid id, ImportMark mark, CancellationToken ct);

    /// <summary>The envelope's cost reports, by holding then year.</summary>
    Task<IReadOnlyList<CostReport>> CostReportsAsync(ItemId envelope, CancellationToken ct);

    /// <summary>Creates the holding's report of that year, or replaces it in place.</summary>
    Task SaveCostReportAsync(ItemId envelope, CostReport report, CancellationToken ct);

    /// <summary>The open holdings of these envelopes maturing in [<paramref name="from"/>, <paramref name="until"/>].</summary>
    Task<IReadOnlyList<MaturingHolding>> MaturingAsync(IReadOnlyCollection<ItemId> envelopes, LocalDate from, LocalDate until, CancellationToken ct);

    /// <summary>The open SCPI holdings of these envelopes that have an entitlement delay, with their purchases.</summary>
    Task<IReadOnlyList<ScpiHolding>> ScpisAsync(IReadOnlyCollection<ItemId> envelopes, CancellationToken ct);
}

public enum PriceStatus { Accepted, Rejected }

/// <summary>Where an ISIN is found at each source (holding prices spec §2.3); <see cref="NotFoundAt"/> is set when no source knows it.</summary>
public sealed record SecurityListing(string Isin, string? BoursoramaPath, string? YahooSymbol, Instant ResolvedAt, Instant? NotFoundAt);

/// <summary>A quote as the source gives it, on the source's date, in the source's currency (spec §2.4).</summary>
public sealed record SecurityPrice(
    string Isin, LocalDate OnDate, decimal Price, Currency Currency, string Source, PriceStatus Status, decimal? Previous, Instant FetchedAt,
    PersonId? AcceptedBy);

/// <summary>An ISIN's latest accepted price and its latest rejected one, the latter only when more recent than the former.</summary>
public sealed record LatestPrices(SecurityPrice? Accepted, SecurityPrice? Rejected);

/// <summary>The unit price of a holding's latest snapshot line, with the envelope it is in (its currency is the caller's to resolve).</summary>
public sealed record SnapshotPrice(string Isin, decimal UnitPrice, ItemId Envelope, LocalDate AsOf);

public interface ISecurityPriceStore
{
    Task<IReadOnlyDictionary<string, SecurityListing>> ListingsAsync(IReadOnlyCollection<string> isins, CancellationToken ct);

    /// <summary>Creates the listing or replaces the one of that ISIN.</summary>
    Task SaveListingAsync(SecurityListing listing, CancellationToken ct);

    /// <summary>Per ISIN, its latest <see cref="PriceStatus.Accepted"/> price; an ISIN without one is absent.</summary>
    Task<IReadOnlyDictionary<string, SecurityPrice>> LatestAcceptedAsync(IReadOnlyCollection<string> isins, CancellationToken ct);

    /// <summary>Per ISIN, its latest accepted and latest rejected prices; an ISIN with neither is absent.</summary>
    Task<IReadOnlyDictionary<string, LatestPrices>> LatestAsync(IReadOnlyCollection<string> isins, CancellationToken ct);

    /// <summary>The accepted prices dated from <paramref name="from"/> to <paramref name="to"/>, both included.</summary>
    Task<IReadOnlyList<SecurityPrice>> AcceptedOnOrBeforeAsync(IReadOnlyCollection<string> isins, LocalDate from, LocalDate to, CancellationToken ct);

    /// <summary>Creates the price or replaces the one of that ISIN and date, atomically; false when it kept a row a person accepted.</summary>
    Task<bool> WriteAsync(SecurityPrice price, CancellationToken ct);

    /// <summary>Turns a rejected price into an accepted one, by <paramref name="by"/> at <paramref name="at"/>; false when that day has no rejected price.</summary>
    Task<bool> AcceptAsync(string isin, LocalDate onDate, PersonId by, Instant at, CancellationToken ct);

    /// <summary>Deletes the prices dated before <paramref name="before"/>.</summary>
    Task<int> PurgeBeforeAsync(LocalDate before, CancellationToken ct);
}

public enum PriceRunStatus { Running, Succeeded, Failed }

/// <summary>One nightly collection, <c>investments.price_run</c> (spec §2.5): counters only, no ISIN.</summary>
public sealed record PriceRun(
    Guid Id, Instant StartedAt, Instant? FinishedAt, PriceRunStatus Status, int Queried, int Accepted, int Rejected, int NotFound, int SourceErrors,
    string? FailureCode, string? FailureMessage);

public interface IPriceRunLog
{
    /// <summary>
    /// Starts a run, or returns <c>null</c> when one is already running: the database holds at most one running row.
    /// A run still marked running an hour after it started is closed as <see cref="PriceCodes.RunAbandoned"/> first.
    /// </summary>
    Task<PriceRun?> TryStartAsync(CancellationToken ct);

    Task FinishAsync(PriceRun run, CancellationToken ct);

    Task<bool> IsRunningAsync(CancellationToken ct);

    /// <summary>The most recent runs first.</summary>
    Task<IReadOnlyList<PriceRun>> RecentAsync(int count, CancellationToken ct);

    /// <summary>One page of runs, newest first, and the total number of runs.</summary>
    Task<(IReadOnlyList<PriceRun> Items, int Total)> PageAsync(int skip, int take, CancellationToken ct);

    /// <summary>Deletes finished runs started before <paramref name="before"/>.</summary>
    Task<int> PurgeBeforeAsync(Instant before, CancellationToken ct);
}

/// <summary>The bytes a source answered; <c>null</c> is a 404 (the source does not know the page).</summary>
public sealed record PriceFetch(byte[]? Body);

/// <summary>Fetches the raw bytes of the price sources through the outbound client; never throws, a failure is a <see cref="PriceCodes"/> code.</summary>
public interface IPriceSourceClient
{
    Task<Result<PriceFetch>> BoursoramaSearchAsync(string isin, CancellationToken ct);

    /// <param name="path">A path found by the search, e.g. <c>/bourse/trackers/cours/1rTCW8/</c>.</param>
    Task<Result<PriceFetch>> BoursoramaPageAsync(string path, CancellationToken ct);

    Task<Result<PriceFetch>> YahooSearchAsync(string isin, CancellationToken ct);

    Task<Result<PriceFetch>> YahooChartAsync(string symbol, CancellationToken ct);
}

/// <summary>Starts the <c>prices-daily</c> job now (Quartz in the host); <see cref="PriceCodes.JobsDisabled"/> when jobs are off.</summary>
public interface IPriceJobTrigger
{
    Task<Result> TriggerAsync(CancellationToken ct);
}

public interface ISecurityBreakdownStore
{
    /// <summary>Per ISIN, its most recent breakdown; an ISIN without one is absent.</summary>
    Task<IReadOnlyDictionary<string, SecurityBreakdown>> LatestAsync(IReadOnlyCollection<string> isins, CancellationToken ct);

    /// <summary>Every version of these ISINs, oldest first (export).</summary>
    Task<IReadOnlyList<SecurityBreakdown>> AllAsync(IReadOnlyCollection<string> isins, CancellationToken ct);

    /// <summary>Creates it or replaces the one of the same ISIN and date (keeping that row's id); returns the row's id.</summary>
    Task<Guid> WriteAsync(SecurityBreakdown breakdown, CancellationToken ct);

    Task<bool> DeleteAsync(string isin, LocalDate asOf, CancellationToken ct);

    /// <summary>Sets the source document on the breakdown rows among <paramref name="ids"/>; other ids are ignored.</summary>
    Task StampDocumentAsync(Guid documentId, IReadOnlyCollection<Guid> ids, CancellationToken ct);
}
