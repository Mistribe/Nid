using System.Text.Json.Serialization;
using Nid.Core.Contracts;

namespace Nid.RealEstate.Contracts;

// A member the file may omit is `set`, not `init`: the source-generated reader of .NET 10 and earlier (NativeAOT) gives an
// omitted init-only property its type's default, not its initializer.

// Spec 3F1 §2: the property sections of realestate/README.md §10. Dates stay strings (the section parses them); amounts are
// { amount, currency } in the property's currency.

public sealed record PropertyRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string PropertyType { get; init; }
    public required string Currency { get; init; }
    public required string PurchaseDate { get; init; }
    public required MoneyRecord PurchasePrice { get; init; }
    public MoneyRecord? NotaryFees { get; init; }
    public MoneyRecord? AgencyFees { get; init; }
    public MoneyRecord? OtherAcquisitionCosts { get; init; }

    /// <summary>A string or a JSON number, read as a decimal; written as a string.</summary>
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal? SurfaceM2 { get; init; }

    public string? LocationLabel { get; init; }
    public string ValuationCadence { get; set; } = "yearly";
    public int? StaleAfterDays { get; init; }
    public string? RentalStartsOn { get; init; }
    public string? Note { get; init; }

    /// <summary>The DPE class, A to G.</summary>
    public string? EnergyClass { get; init; }

    /// <summary>Optional, for receipts and letters.</summary>
    public string? PostalAddress { get; init; }
    public required IReadOnlyList<ShareRecord> Ownership { get; init; }
    public string Visibility { get; set; } = "shared";

    /// <summary>#294: <c>view</c> when omitted, <c>edit</c> lets every adult write the shared item.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShareLevel { get; set; }
}

/// <summary>A planned cost's period: <c>{ count, unit }</c>, months or years.</summary>
public sealed record PeriodRecord
{
    public required int Count { get; init; }
    public required string Unit { get; init; }
}

public sealed record PropertyRecurringCostRecord
{
    public required string Id { get; init; }
    public required string Property { get; init; }
    public required string Category { get; init; }
    public required MoneyRecord Amount { get; init; }
    public required PeriodRecord Every { get; init; }
    public required string StartsOn { get; init; }
    public string? EndsOn { get; init; }
    public string? Note { get; init; }
}

public sealed record TenancyRecord
{
    public required string Id { get; init; }
    public required string Property { get; init; }
    public required string Label { get; init; }
    public required string StartsOn { get; init; }
    public string? EndsOn { get; init; }
    public required MoneyRecord RentMonthly { get; init; }
    public MoneyRecord? ChargesMonthly { get; init; }
    public MoneyRecord? Deposit { get; init; }
    public string? Note { get; init; }
    public bool Indexed { get; init; }
    public int? IrlReferenceQuarter { get; init; }
    public string? TenantNames { get; init; }
    public string? LandlordName { get; init; }
    public string? LandlordAddress { get; init; }

    /// <summary>ISO date, the first day of the month of the first rent recorded in Nid.</summary>
    public string? RentsTrackedFrom { get; init; }
}
