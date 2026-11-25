using NodaTime;

namespace Nid.Assets.Domain;

/// <summary>An asset's value on a date: a valuation, or the purchase price when none is dated yet.</summary>
public sealed record AssetValue(decimal Amount, LocalDate On, bool FromPurchase);

public static class AssetMath
{
    /// <summary>The latest valuation on or before <paramref name="date"/>; else the purchase price from its date; else null.</summary>
    public static AssetValue? At(AssetDetails d, IReadOnlyList<(LocalDate On, decimal Amount)> valuations, LocalDate date)
    {
        var latest = valuations.Where(v => v.On <= date).OrderByDescending(v => v.On).Select(v => ((LocalDate On, decimal Amount)?)v).FirstOrDefault();
        if (latest is { } v)
            return new AssetValue(v.Amount, v.On, false);
        return d is { PurchasePrice: { } price, PurchasedOn: { } on } && on <= date ? new AssetValue(price, on, true) : null;
    }

    /// <summary>The earliest of the purchase price on its date and the first valuation on its date: what goes in <c>Moved</c>.</summary>
    public static AssetValue? FirstCounted(AssetDetails d, IReadOnlyList<(LocalDate On, decimal Amount)> valuations)
    {
        var first = valuations.OrderBy(v => v.On).Select(v => ((LocalDate On, decimal Amount)?)v).FirstOrDefault();
        var purchase = d is { PurchasePrice: { } price, PurchasedOn: { } on } ? new AssetValue(price, on, true) : null;
        if (first is not { } f)
            return purchase;
        return purchase is not null && purchase.On <= f.On ? purchase : new AssetValue(f.Amount, f.On, false);
    }
}
