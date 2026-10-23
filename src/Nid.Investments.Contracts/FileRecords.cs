using System.Text.Json.Serialization;
using Nid.Core.Contracts;

namespace Nid.Investments.Contracts;

/// <summary>The entity of investments-data-model.md §9, as the file spells it; dates stay strings (the section parses them).</summary>
public sealed record EnvelopeRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public string? Institution { get; init; }
    public string? AccountHint { get; init; }
    public string? Note { get; init; }
    public string? OpenedOn { get; init; }
    public string? ClosedOn { get; init; }
    public required string Currency { get; init; }
    public string ValuationCadence { get; init; } = "monthly";
    public int? StaleAfterDays { get; init; }

    /// <summary>Written only when set, so a file of before the field imports with the same hash.</summary>
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("premiums_before_2017")] // the snake case policy writes no underscore before digits
    public decimal? PremiumsBefore2017 { get; init; }

    public required IReadOnlyList<ShareRecord> Ownership { get; init; }
    public string Visibility { get; init; } = "shared";
}

/// <summary>
/// A holding of the file (investments-data-model.md §9): <see cref="Item"/> is its envelope's file id. Percentages and units are read
/// as decimals, from a string or a JSON number, and written as strings.
/// </summary>
public sealed record HoldingRecord
{
    public required string Id { get; init; }
    public required string Item { get; init; }
    public required string Name { get; init; }
    public string? Isin { get; init; }
    public required string Kind { get; init; }
    public string? AssetClass { get; init; }
    public string? Issuer { get; init; }
    public int? RiskIndicator { get; init; }
    public string? OpenedOn { get; init; }
    public string? MaturesOn { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal? EntryCostPct { get; init; }

    public string? ClosedOn { get; init; }
    public string? Note { get; init; }

    /// <summary>Written only when false, so a file of before the field imports with the same hash.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AutoPrice { get; init; }
}

/// <summary>A snapshot of an envelope's holdings at a date, its lines nested; a line names a holding by its file id.</summary>
public sealed record HoldingSnapshotRecord
{
    public required string Id { get; init; }
    public required string Item { get; init; }
    public required string AsOf { get; init; }
    public string? SourceLabel { get; init; }
    public bool Complete { get; init; } = true;
    public required IReadOnlyList<SnapshotLineRecord> Lines { get; init; }
}

public sealed record SnapshotLineRecord
{
    public required string Holding { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal? Units { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal? UnitPrice { get; init; }

    public required MoneyRecord Value { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal? CostBasisPerUnit { get; init; }
}

/// <summary>
/// A movement on a holding (by its file id); units are signed by the kind. <see cref="EnvelopeFlow"/> names a flow of the file on the
/// same envelope; the two legs of a switch share <see cref="SwitchRef"/>.
/// </summary>
public sealed record HoldingMovementRecord
{
    public required string Id { get; init; }
    public required string Holding { get; init; }
    public required string OccurredOn { get; init; }
    public required string Kind { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal? Units { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal? UnitPrice { get; init; }

    public required MoneyRecord Amount { get; init; }
    public string? EnvelopeFlow { get; init; }
    public string? SwitchRef { get; init; }
    public string? Label { get; init; }
    public string? ExternalRef { get; init; }
}
