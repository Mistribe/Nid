using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Investments.Contracts;

/// <summary>Holdings spec §2.1: the wire values of a holding's kind and asset class.</summary>
public static class HoldingWire
{
    public static readonly IReadOnlyList<string> Kinds = ["euro_fund", "fund", "etf", "equity", "bond", "structured", "cash", "other"];

    public static readonly IReadOnlyList<string> AssetClasses = ["equity", "bond", "money_market", "mixed", "real_estate", "capital_guaranteed", "other"];

    /// <summary>A euro fund and cash are a capital: no units, no unit price.</summary>
    public static bool HasUnits(string kind) => kind is not ("euro_fund" or "cash");
}

/// <summary>Issue #34 §2.3: a movement's kind, its direction, and whether it is money in or out of the holding.</summary>
public static class MovementKinds
{
    public const string AllocationIn = "allocation_in";
    public const string AllocationOut = "allocation_out";
    public const string SwitchIn = "switch_in";
    public const string SwitchOut = "switch_out";
    public const string Maturity = "maturity";
    public const string Fee = "fee";
    public const string Interest = "interest";
    public const string Income = "income";

    public static readonly IReadOnlyList<string> All = [AllocationIn, AllocationOut, SwitchIn, SwitchOut, Maturity, Fee, Interest, Income];

    /// <summary>Money in of the holding's performance.</summary>
    public static bool IsMoneyIn(string kind) => kind is AllocationIn or SwitchIn;

    /// <summary>Money out of the holding's performance.</summary>
    public static bool IsMoneyOut(string kind) => kind is AllocationOut or SwitchOut or Maturity;

    /// <summary>Units come in (bought, reinvested); the others take units out.</summary>
    public static bool IsIn(string kind) => kind is AllocationIn or SwitchIn or Interest or Income;
}

/// <summary>One line of an envelope (holdings spec §2.1): a fund, a share, the euro fund, a structured product, cash.</summary>
public sealed record EnvelopeHolding(
    Guid Id, ItemId Envelope, string Name, string? Isin, string Kind, string? AssetClass, string? Issuer, int? RiskIndicator,
    LocalDate? OpenedOn, LocalDate? MaturesOn, decimal? EntryCostPct, LocalDate? ClosedOn, string? Note, bool AutoPrice = true);

public sealed record StoredHolding(EnvelopeHolding Holding, uint Version);

/// <summary>A holding in a snapshot: <see cref="Value"/> as printed, in the envelope's currency; units and prices with 6 decimals.</summary>
public sealed record SnapshotLine(Guid HoldingId, decimal? Units, decimal? UnitPrice, decimal Value, decimal? CostBasisPerUnit);

/// <summary>The composition of an envelope at a date (holdings spec §2.2): one per date.</summary>
public sealed record HoldingSnapshot(Guid Id, ItemId Envelope, LocalDate AsOf, string? SourceLabel, bool Complete, IReadOnlyList<SnapshotLine> Lines);

/// <summary>
/// A dated movement on one holding (issue #34 §2.3): <see cref="Amount"/> is above zero, its direction comes from the kind; units are
/// signed (bought +, sold or deducted −). The two legs of a switch share <see cref="SwitchRef"/>.
/// </summary>
public sealed record HoldingMovement(
    Guid Id, ItemId Envelope, Guid HoldingId, LocalDate OccurredOn, string Kind, decimal? Units, decimal? UnitPrice, decimal Amount,
    Guid? EnvelopeFlowId, string? SwitchRef, string? Label, string? ExternalRef);

/// <summary>Issue #34 §3, a holding at its latest valued snapshot on or before the date: money in and out, gain, XIRR, explicit fees.</summary>
public sealed record HoldingFigures(
    LocalDate AsOf, Money Value, Money MoneyIn, Money MoneyOut, Money Gain, decimal? GainPct, decimal? AnnualReturnPct,
    Money ExplicitFees, Money GainBeforeFees);

public static class HoldingWarnings
{
    public const string ValueMismatch = "holding_value_mismatch";
    public const string SumMismatch = "holdings_sum_mismatch";
    public const string AllocationMismatch = "allocation_mismatch";
    public const string SwitchUnbalanced = "switch_unbalanced";
    public const string CostReportInconsistent = "cost_report_inconsistent";
    public const string UnexplainedUnits = "unexplained_units";
    public const string UcShareBelowFloor = "uc_share_below_floor";
}

/// <summary>
/// A warning about a snapshot or movements, on one holding when <see cref="HoldingId"/> is set, on a date when <see cref="On"/> is.
/// <c>unexplained_units</c> carries the <see cref="Units"/> no movement explains and, when units went missing, the <see cref="ProposedFee"/>
/// at the later unit price (never written).
/// </summary>
public sealed record HoldingWarning(string Code, Guid? HoldingId = null, LocalDate? On = null, decimal? Units = null, Money? ProposedFee = null);

/// <summary>
/// Issue #34 §2.5: a holding's yearly gross return, total fees and net return, in percent, as stated on the annual document (art. A.522-1);
/// never derived from snapshots.
/// </summary>
public sealed record CostReport(
    Guid HoldingId, int Year, decimal GrossReturnPct, decimal TotalFeesPct, decimal NetReturnPct, decimal? ContractFeesPct, decimal? SupportFeesPct,
    decimal? RetrocessionPct, decimal? NetAnnualized5yPct, string? SourceLabel);

/// <summary>A holding with a maturity date, for the upcoming events.</summary>
public sealed record MaturingHolding(ItemId Envelope, Guid HoldingId, string Name, LocalDate MaturesOn);

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

    /// <summary>Every field of <paramref name="holding"/>, in place.</summary>
    Task ReplaceAsync(EnvelopeHolding holding, CancellationToken ct);

    Task DeleteAsync(Guid holding, CancellationToken ct);

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
}
