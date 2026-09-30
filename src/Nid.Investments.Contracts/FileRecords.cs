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
    public required IReadOnlyList<ShareRecord> Ownership { get; init; }
    public string Visibility { get; init; } = "shared";
}
