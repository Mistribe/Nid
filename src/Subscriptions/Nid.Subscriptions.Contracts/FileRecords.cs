using System.Text.Json.Serialization;
using Nid.Core.Contracts;

namespace Nid.Subscriptions.Contracts;

// A member the file may omit is `set`, not `init`: the source-generated reader of .NET 10 and earlier (NativeAOT) gives an
// omitted init-only property its type's default, not its initializer.

// The entities of import-format.md §5.3 to §5.5, exactly as the file spells them. Dates, units and kinds stay
// strings here: the section parses them, so each refusal names the member it concerns.

public sealed record DurationRecord
{
    public required int Count { get; init; }
    public required string Unit { get; init; }
}

public sealed record PayerRecord
{
    public required string Type { get; init; }
    public string? Person { get; init; }
}

public sealed record SubscriptionRecord
{
    public required string Id { get; init; }
    public string? ProviderKey { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<ShareRecord> Ownership { get; init; }
    public string Visibility { get; set; } = "shared";

    /// <summary>#294: <c>view</c> when omitted, <c>edit</c> lets every adult write the shared item.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShareLevel { get; set; }

    public IReadOnlyList<string> ServiceUsers { get; set; } = [];
    public required bool AutoRenew { get; init; }
    public string? CancelledAt { get; init; }

    // Contract terms (issue #208). Omitted when null, as the export writes them: a file from before the terms hashes
    // as it always did, and an export of a subscription without terms is the file it was imported from.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AnniversaryOn { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DurationRecord? Notice { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CommitmentEndsOn { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CancellableFrom { get; init; }

    public string? Note { get; init; }
}

public sealed record BillingPhaseRecord
{
    public required string Id { get; init; }
    public required string SubscriptionId { get; init; }
    public required string StartsOn { get; init; }
    public string? EndsOn { get; init; }
    public required MoneyRecord Price { get; init; }
    public required DurationRecord Every { get; init; }
    public DurationRecord? Covers { get; init; }
    public int? MaxCycles { get; init; }
    public required PayerRecord Payer { get; init; }
    public string? Note { get; init; }
}

public sealed record GrantRecord
{
    public required string SubscriptionId { get; init; }
    public required DurationRecord Duration { get; init; }
    public required string Source { get; init; }
    public string? StartsOn { get; init; }
}

public sealed record PurchaseRecord
{
    public required string Id { get; init; }
    public required string Date { get; init; }
    public required string Label { get; init; }
    public required string Kind { get; init; }
    public required MoneyRecord Price { get; init; }
    public required PayerRecord Payer { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal? AllocatedAmount { get; init; }

    public required IReadOnlyList<GrantRecord> Grants { get; init; }
    public string? Note { get; init; }
}

/// <summary>One guarantee of a policy (issue #208): ceiling and deductible are text, never parsed.</summary>
public sealed record GuaranteeRecord
{
    public required string Label { get; init; }
    public string? Ceiling { get; init; }
    public string? Deductible { get; init; }
}

/// <summary>
/// import-format.md §5.3bis: the insurance of a subscription (issue #208). <c>subscription</c> and <c>property</c> name an
/// entity of this file; at most one insurance names a subscription.
/// </summary>
public sealed record InsuranceRecord
{
    public required string Id { get; init; }
    public required string Subscription { get; init; }
    public required string Kind { get; init; }
    public string? Property { get; init; }
    public string? InsuredLabel { get; init; }
    public string? PolicyNumber { get; init; }
    public string? InsurerContact { get; init; }

    // `set`, not `init`: see the note at the top of this file.
    public IReadOnlyList<GuaranteeRecord> Guarantees { get; set; } = [];
}

/// <summary>import-format.md §5.2: a custom provider. <c>key</c> is local to the file (design §3.3), mapped at commit.</summary>
public sealed record ProviderRecord
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? Website { get; init; }
    public string? PricingPage { get; init; }
    public required IReadOnlyList<string> LabelKeys { get; init; }
}
