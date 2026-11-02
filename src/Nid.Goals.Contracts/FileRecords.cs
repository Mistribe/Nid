using System.Text.Json.Serialization;
using Nid.Core.Contracts;

namespace Nid.Goals.Contracts;

// A member the file may omit is `set`, not `init`: the source-generated reader of .NET 10 and earlier (NativeAOT) gives an
// omitted init-only property its type's default, not its initializer.

/// <summary>The goal of <c>nid.data</c> (savings goals spec D16); dates stay strings, the section parses them. Amounts read as decimals, never doubles.</summary>
public sealed record GoalRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal? TargetAmount { get; init; }

    public int? Months { get; init; }
    public string? TargetDate { get; init; }
    public string? Note { get; init; }
    public string Visibility { get; set; } = "shared";

    /// <summary>#294: <c>view</c> when omitted, <c>edit</c> lets every adult write the shared item.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShareLevel { get; set; }

    public required IReadOnlyList<ShareRecord> Ownership { get; init; }
    public bool Closed { get; init; }
    public IReadOnlyList<GoalFundingRecord> Funding { get; set; } = [];
    public IReadOnlyList<GoalEntryRecord> Entries { get; set; } = [];
}

/// <summary>A link to an envelope: <c>envelope</c> is the file id of an <c>envelopes</c> entity.</summary>
public sealed record GoalFundingRecord
{
    public required string Envelope { get; init; }
    public required int ShareBp { get; init; }
}

public sealed record GoalEntryRecord
{
    public required string On { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public required decimal Amount { get; init; }
}
