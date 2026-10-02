using Nid.Core.Contracts;

namespace Nid.Income.Contracts;

/// <summary>The source of income-data-model.md §9, plus <c>ended_on</c> (2C1 spec §5); dates stay strings (the section parses them).</summary>
public sealed record IncomeSourceRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public string? PayerLabel { get; init; }
    public string? Note { get; init; }
    public required string Currency { get; init; }
    public required IReadOnlyList<ShareRecord> Ownership { get; init; }
    public string Visibility { get; init; } = "shared";
    public string? EndedOn { get; init; }
}

public sealed record IncomeDurationRecord
{
    public required int Count { get; init; }
    public required string Unit { get; init; }
}

/// <summary>The phase of income-data-model.md §9; <c>source</c> is the file id of an <c>income_sources</c> entity.</summary>
public sealed record IncomePhaseRecord
{
    public required string Id { get; init; }
    public required string Source { get; init; }
    public required string Label { get; init; }
    public required string StartsOn { get; init; }
    public string? EndsOn { get; init; }
    public required MoneyRecord NetAmount { get; init; }
    public required IncomeDurationRecord Every { get; init; }
    public MoneyRecord? GrossAmount { get; init; }
    public int? MaxCycles { get; init; }
    public string? Note { get; init; }
}
