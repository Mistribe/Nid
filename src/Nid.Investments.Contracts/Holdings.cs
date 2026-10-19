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

/// <summary>One line of an envelope (holdings spec §2.1): a fund, a share, the euro fund, a structured product, cash.</summary>
public sealed record EnvelopeHolding(
    Guid Id, ItemId Envelope, string Name, string? Isin, string Kind, string? AssetClass, string? Issuer, int? RiskIndicator,
    LocalDate? OpenedOn, LocalDate? MaturesOn, decimal? EntryCostPct, LocalDate? ClosedOn, string? Note);

public sealed record StoredHolding(EnvelopeHolding Holding, uint Version);

/// <summary>A holding in a snapshot: <see cref="Value"/> as printed, in the envelope's currency; units and prices with 6 decimals.</summary>
public sealed record SnapshotLine(Guid HoldingId, decimal? Units, decimal? UnitPrice, decimal Value, decimal? CostBasisPerUnit);

/// <summary>The composition of an envelope at a date (holdings spec §2.2): one per date.</summary>
public sealed record HoldingSnapshot(Guid Id, ItemId Envelope, LocalDate AsOf, string? SourceLabel, bool Complete, IReadOnlyList<SnapshotLine> Lines);

public static class HoldingWarnings
{
    public const string ValueMismatch = "holding_value_mismatch";
    public const string SumMismatch = "holdings_sum_mismatch";
}

/// <summary>A warning about a snapshot, on one of its lines when <see cref="HoldingId"/> is set.</summary>
public sealed record HoldingWarning(string Code, Guid? HoldingId = null);

public interface IHoldingStore
{
    Task<IReadOnlyList<StoredHolding>> HoldingsAsync(ItemId envelope, CancellationToken ct);

    Task AddAsync(EnvelopeHolding holding, CancellationToken ct);

    /// <summary>Every field of <paramref name="holding"/>, in place.</summary>
    Task ReplaceAsync(EnvelopeHolding holding, CancellationToken ct);

    Task DeleteAsync(Guid holding, CancellationToken ct);

    /// <summary>True when a snapshot line names the holding.</summary>
    Task<bool> InUseAsync(Guid holding, CancellationToken ct);

    /// <summary>The envelope's snapshots, latest first.</summary>
    Task<IReadOnlyList<HoldingSnapshot>> SnapshotsAsync(ItemId envelope, CancellationToken ct);

    /// <summary>Creates the snapshot, or replaces the one of that id, lines included.</summary>
    Task SaveSnapshotAsync(HoldingSnapshot snapshot, CancellationToken ct);

    Task DeleteSnapshotAsync(Guid snapshot, CancellationToken ct);
}
