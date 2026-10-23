using NodaTime;

namespace Nid.Investments.Infrastructure.Persistence;

/// <summary>A holdings row an import marks (import-format.md §8): the file id it came from, the entity's hash, its importer.</summary>
public interface IImportMarked
{
    Guid Id { get; }
    Guid EnvelopeItemId { get; }
    string? ImportRef { get; set; }
    string? ImportHash { get; set; }
    Guid? ImportedBy { get; set; }
}

/// <summary>investments.holding (holdings spec §2.1); <c>Version</c> is <c>xmin</c>.</summary>
public sealed class HoldingRow : IImportMarked
{
    public Guid Id { get; set; }
    public Guid EnvelopeItemId { get; set; }
    public string Name { get; set; } = "";
    public string? Isin { get; set; }
    public string Kind { get; set; } = "";
    public string? AssetClass { get; set; }
    public string? Issuer { get; set; }
    public int? RiskIndicator { get; set; }
    public LocalDate? OpenedOn { get; set; }
    public LocalDate? MaturesOn { get; set; }
    public decimal? EntryCostPct { get; set; }
    public LocalDate? ClosedOn { get; set; }
    public string? Note { get; set; }
    /// <summary>Whether the nightly collection prices the holding; without effect without an ISIN (holding prices spec §2.2).</summary>
    public bool AutoPrice { get; set; } = true;
    public string? ImportRef { get; set; }
    public string? ImportHash { get; set; }
    public Guid? ImportedBy { get; set; }
    public uint Version { get; set; }
}

/// <summary>investments.holding_snapshot (holdings spec §2.2): one per envelope and date.</summary>
public sealed class HoldingSnapshotRow : IImportMarked
{
    public Guid Id { get; set; }
    public Guid EnvelopeItemId { get; set; }
    public LocalDate AsOf { get; set; }
    public string? SourceLabel { get; set; }
    public bool Complete { get; set; }
    public string? ImportRef { get; set; }
    public string? ImportHash { get; set; }
    public Guid? ImportedBy { get; set; }
    public List<HoldingSnapshotLineRow> Lines { get; set; } = [];
}

/// <summary>investments.holding_snapshot_line; <c>EnvelopeItemId</c> is the snapshot's, so its audit lands on the envelope.</summary>
public sealed class HoldingSnapshotLineRow
{
    public Guid SnapshotId { get; set; }
    public Guid HoldingId { get; set; }
    public Guid EnvelopeItemId { get; set; }
    public decimal? Units { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal Value { get; set; }
    public decimal? CostBasisPerUnit { get; set; }
}

/// <summary>investments.holding_cost_report (issue #34 §2.5): one per holding and year; <c>EnvelopeItemId</c> is the holding's, for the audit.</summary>
public sealed class HoldingCostReportRow
{
    public Guid HoldingId { get; set; }
    public int Year { get; set; }
    public Guid EnvelopeItemId { get; set; }
    public decimal GrossReturnPct { get; set; }
    public decimal TotalFeesPct { get; set; }
    public decimal NetReturnPct { get; set; }
    public decimal? ContractFeesPct { get; set; }
    public decimal? SupportFeesPct { get; set; }
    public decimal? RetrocessionPct { get; set; }
    public decimal? NetAnnualized5yPct { get; set; }
    public string? SourceLabel { get; set; }
}

/// <summary>investments.holding_movement (issue #34 §2.3); <c>EnvelopeItemId</c> is the holding's, so its audit lands on the envelope.</summary>
public sealed class HoldingMovementRow : IImportMarked
{
    public Guid Id { get; set; }
    public Guid HoldingId { get; set; }
    public Guid EnvelopeItemId { get; set; }
    public LocalDate OccurredOn { get; set; }
    public string Kind { get; set; } = "";
    public decimal? Units { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal Amount { get; set; }
    public Guid? EnvelopeFlowId { get; set; }
    public string? SwitchRef { get; set; }
    public string? Label { get; set; }
    public string? ExternalRef { get; set; }
    public string? ImportRef { get; set; }
    public string? ImportHash { get; set; }
    public Guid? ImportedBy { get; set; }
}
