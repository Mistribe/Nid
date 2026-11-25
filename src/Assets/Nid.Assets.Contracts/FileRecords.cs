using System.Text.Json.Serialization;
using Nid.Core.Contracts;

namespace Nid.Assets.Contracts;

// A member the file may omit is `set`, not `init`: the source-generated reader of .NET 10 and earlier (NativeAOT) gives an
// omitted init-only property its type's default, not its initializer.

/// <summary>
/// The asset of <c>nid.data</c> (#186): its value is the purchase price, or the valuations of the file naming it in <c>item</c>.
/// Dates stay strings, the section parses them; amounts read as decimals, never doubles.
/// </summary>
public sealed record AssetImportRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string Currency { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal? PurchasePrice { get; init; }

    public string? PurchasedOn { get; init; }
    public string? Note { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal? WeightGrams { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal? Fineness { get; init; }

    public string? RegisteredOn { get; set; }
    public int? MileageKm { get; set; }
    public string? MileageOn { get; set; }
    public int? FiscalHp { get; set; }
    public string? Energy { get; set; }

    public string Visibility { get; set; } = "shared";

    /// <summary><c>view</c> when omitted, <c>edit</c> lets every adult write the shared item.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShareLevel { get; set; }

    public required IReadOnlyList<ShareRecord> Ownership { get; init; }
    public bool Closed { get; init; }
}
