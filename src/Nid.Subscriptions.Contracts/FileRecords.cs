using System.Text.Json.Serialization;

namespace Nid.Subscriptions.Contracts;

// The entities of import-format.md §5.3 to §5.5, exactly as the file spells them. Dates, units and kinds stay
// strings here: the section parses them, so each refusal names the member it concerns.

public sealed record MoneyRecord
{
    /// <summary>A string or a JSON number, read as decimal either way — never as a double (§2).</summary>
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public required decimal Amount { get; init; }

    public required string Currency { get; init; }
}

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

public sealed record ShareRecord
{
    public required string Person { get; init; }
    public required int ShareBp { get; init; }
}

public sealed record SubscriptionRecord
{
    public required string Id { get; init; }
    public string? ProviderKey { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<ShareRecord> Ownership { get; init; }
    public string Visibility { get; init; } = "shared";
    public IReadOnlyList<string> ServiceUsers { get; init; } = [];
    public required bool AutoRenew { get; init; }
    public string? CancelledAt { get; init; }
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

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public decimal? AllocatedAmount { get; init; }

    public required IReadOnlyList<GrantRecord> Grants { get; init; }
    public string? Note { get; init; }
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
