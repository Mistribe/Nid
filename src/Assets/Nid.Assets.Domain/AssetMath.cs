using NodaTime;

namespace Nid.Assets.Domain;

/// <summary>An asset's value on a date: a valuation, or the purchase price when none is dated yet.</summary>
/// <param name="On">The date of the base (the valuation or the purchase), also for an estimate.</param>
/// <param name="Estimated">The amount is the base worn down by the vehicle's depreciation rule; <paramref name="BaseAmount"/> is that base.</param>
public sealed record AssetValue(decimal Amount, LocalDate On, bool FromPurchase, bool Estimated = false, decimal? BaseAmount = null);

public static class AssetMath
{
    /// <summary>The latest valuation on or before <paramref name="date"/>; else the purchase price from its date; else null.</summary>
    public static AssetValue? At(AssetDetails d, IReadOnlyList<(LocalDate On, decimal Amount)> valuations, LocalDate date)
        => Estimate(d, Base(d, valuations, date), date);

    /// <summary>
    /// With a depreciation rule r (yearly %): base × (1 − r/100)^(days since the base / 365), rounded to cents (half away from zero), never
    /// above the base. A valuation dated later is a newer base, so the estimate restarts from it.
    /// </summary>
    private static AssetValue? Estimate(AssetDetails d, AssetValue? b, LocalDate date)
    {
        if (b is null || d.DepreciationRate is not { } rate || date <= b.On)
            return b;
        var factor = (decimal)Math.Pow((double)(1m - rate / 100m), Period.Between(b.On, date, PeriodUnits.Days).Days / 365.0);
        var amount = Math.Min(b.Amount, decimal.Round(b.Amount * factor, 2, MidpointRounding.AwayFromZero));
        return b with { Amount = amount, Estimated = true, BaseAmount = b.Amount };
    }

    private static AssetValue? Base(AssetDetails d, IReadOnlyList<(LocalDate On, decimal Amount)> valuations, LocalDate date)
    {
        var past = valuations.Where(v => v.On <= date).ToList();
        if (past.Count > 0)
        {
            var v = past.MaxBy(x => x.On);
            return new AssetValue(v.Amount, v.On, false);
        }
        return d is { PurchasePrice: { } price, PurchasedOn: { } on } && on <= date ? new AssetValue(price, on, true) : null;
    }

    /// <summary>The earliest of the purchase price on its date and the first valuation on its date: what goes in <c>Moved</c>.</summary>
    public static AssetValue? FirstCounted(AssetDetails d, IReadOnlyList<(LocalDate On, decimal Amount)> valuations)
    {
        var purchase = d is { PurchasePrice: { } price, PurchasedOn: { } on } ? new AssetValue(price, on, true) : null;
        if (valuations.Count == 0)
            return purchase;
        var f = valuations.MinBy(v => v.On);
        return purchase is not null && purchase.On <= f.On ? purchase : new AssetValue(f.Amount, f.On, false);
    }
}
