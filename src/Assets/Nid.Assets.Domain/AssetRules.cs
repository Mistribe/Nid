using Nid.Assets.Contracts;
using NodaTime;
using UnambitiousFx.Functional.Failures;

namespace Nid.Assets.Domain;

/// <summary>The editable fields of an asset, as a request sends them: an edit replaces every one.</summary>
public sealed record AssetDetails(
    string Kind, decimal? PurchasePrice, LocalDate? PurchasedOn, string? Note, decimal? WeightGrams, decimal? Fineness);

/// <summary>The bounds of an asset's fields. The first refusal wins; null when there is none.</summary>
public static class AssetRules
{
    public const decimal FinenessMax = 1000m;
    private const decimal AmountBound = 1_000_000_000_000m;
    private const decimal WeightBound = 1_000_000_000m; // numeric(12,3)

    public static Failure? Check(AssetDetails d, LocalDate today)
    {
        if (!AssetKinds.All.Contains(d.Kind))
            return Refuse(AssetsCodes.UnknownKind, "kind", $"'{d.Kind}' is not an asset kind.");
        if ((d.PurchasePrice is null) != (d.PurchasedOn is null))
            return Refuse(AssetsCodes.PurchaseIncomplete, d.PurchasePrice is null ? "purchasePrice" : "purchasedOn", "A purchase has a price and a date, or neither.");
        if (d.PurchasePrice is { } price)
        {
            if (price <= 0m || decimal.Round(price, 2) != price || price >= AmountBound)
                return Refuse(AssetsCodes.InvalidPurchase, "purchasePrice", "A price is above zero, with at most two decimals, below 10^12.");
            if (d.PurchasedOn > today)
                return Refuse(AssetsCodes.InvalidPurchase, "purchasedOn", "A purchase is not dated after today.");
        }
        if (d.Kind != AssetKinds.PreciousMetal)
            return d.WeightGrams is not null ? Refuse(AssetsCodes.MetalOnly, "weightGrams", "A weight is for a precious metal only.")
                : d.Fineness is not null ? Refuse(AssetsCodes.MetalOnly, "fineness", "A fineness is for a precious metal only.")
                : null;
        if (d.WeightGrams is { } w && (w <= 0m || w >= WeightBound || decimal.Round(w, 3) != w))
            return Refuse(AssetsCodes.InvalidWeight, "weightGrams", "A weight is above zero, with at most three decimals.");
        if (d.Fineness is { } f && (f <= 0m || f > FinenessMax || decimal.Round(f, 1) != f))
            return Refuse(AssetsCodes.InvalidFineness, "fineness", "A fineness is above 0 and at most 1000, with at most one decimal.");
        return null;
    }

    /// <summary>A value is always present: a purchase price, or at least one valuation.</summary>
    public static bool HasValue(AssetDetails d, int valuationCount) => d.PurchasePrice is not null || valuationCount > 0;

    private static Failure Refuse(string code, string field, string message)
        => new(code, message, new Dictionary<string, object?> { ["field"] = field });
}
