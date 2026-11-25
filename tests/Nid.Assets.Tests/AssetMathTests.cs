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

    private static AssetDetails Car(decimal? rate, decimal price = 10_000m) => new("vehicle", price, new LocalDate(2023, 1, 1), null, null, null, DepreciationRate: rate);

    [Fact]
    public void Estimate_on_the_base_date_is_the_base()
    {
        var v = AssetMath.At(Car(20m), [], new(2023, 1, 1))!;
        Assert.Equal(10_000m, v.Amount);
        Assert.False(v.Estimated);
    }

    [Fact]
    public void Estimate_a_year_later_is_base_times_one_minus_rate()
    {
        var v = AssetMath.At(Car(20m), [], new(2024, 1, 1))!;
        Assert.Equal(8_000m, v.Amount);
        Assert.True(v.Estimated);
        Assert.True(v.FromPurchase);
        Assert.Equal(new LocalDate(2023, 1, 1), v.On);
        Assert.Equal(10_000m, v.BaseAmount);
    }

    [Fact]
    public void Estimate_compounds_over_two_years()
        => Assert.Equal(6_400m, AssetMath.At(Car(20m), [], new(2024, 12, 31))!.Amount); // 730 days

    [Fact]
    public void Estimate_restarts_from_a_newer_valuation()
    {
        var v = AssetMath.At(Car(20m), [(new(2024, 1, 1), 9_000m)], new(2024, 12, 31))!; // 365 days after the valuation
        Assert.Equal(7_200m, v.Amount);
        Assert.False(v.FromPurchase);
        Assert.Equal(new LocalDate(2024, 1, 1), v.On);
        Assert.Equal(9_000m, v.BaseAmount);
    }

    [Fact]
    public void Estimate_is_rounded_to_cents_away_from_zero()
    {
        // 100.05 * 0.9 = 90.045 exactly at one year: the tie goes away from zero.
        Assert.Equal(90.05m, AssetMath.At(new("vehicle", 100.05m, new(2023, 1, 1), null, null, null, DepreciationRate: 10m), [], new(2024, 1, 1))!.Amount);
    }

    [Fact]
    public void Estimate_never_exceeds_the_base_and_without_a_rule_is_the_base()
    {
        Assert.Equal(10_000m, AssetMath.At(Car(null), [], new(2030, 1, 1))!.Amount);
        Assert.False(AssetMath.At(Car(null), [], new(2030, 1, 1))!.Estimated);
        Assert.True(AssetMath.At(Car(0.01m), [], new(2023, 1, 2))!.Amount <= 10_000m);
    }
}
