using Nid.Assets.Domain;
using NodaTime;

namespace Nid.Assets.Tests;

public class AssetMathTests
{
    private static readonly AssetDetails Bare = new("other", null, null, null, null, null);
    private static readonly AssetDetails Bought = new("vehicle", 15_000m, new LocalDate(2023, 3, 1), null, null, null);
    private static readonly (LocalDate On, decimal Amount)[] Two = [(new(2024, 1, 1), 10_000m), (new(2025, 1, 1), 8_000m)];

    [Fact]
    public void At_takes_latest_valuation_on_or_before_date()
    {
        Assert.Equal(10_000m, AssetMath.At(Bare, Two, new(2024, 6, 30))!.Amount);
        Assert.Equal(8_000m, AssetMath.At(Bare, Two, new(2025, 1, 1))!.Amount);
        Assert.Null(AssetMath.At(Bare, Two, new(2023, 12, 31)));
    }

    [Fact]
    public void At_falls_back_to_purchase_price_from_its_date()
    {
        Assert.Null(AssetMath.At(Bought, [], new(2023, 2, 28)));
        var v = AssetMath.At(Bought, [], new(2023, 3, 1))!;
        Assert.Equal(15_000m, v.Amount);
        Assert.True(v.FromPurchase);
    }

    [Fact]
    public void At_prefers_a_valuation_over_the_purchase_price()
    {
        var v = AssetMath.At(Bought, [(new(2023, 6, 1), 12_000m)], new(2023, 7, 1))!;
        Assert.Equal(12_000m, v.Amount);
        Assert.False(v.FromPurchase);
    }

    [Fact]
    public void FirstCounted_is_the_purchase_when_it_comes_first()
    {
        var v = AssetMath.FirstCounted(Bought, Two)!;
        Assert.True(v.FromPurchase);
        Assert.Equal(new LocalDate(2023, 3, 1), v.On);
    }

    [Fact]
    public void FirstCounted_is_the_first_valuation_without_purchase()
    {
        var v = AssetMath.FirstCounted(Bare, Two)!;
        Assert.False(v.FromPurchase);
        Assert.Equal(new LocalDate(2024, 1, 1), v.On);
        Assert.Null(AssetMath.FirstCounted(Bare, []));
    }
}
