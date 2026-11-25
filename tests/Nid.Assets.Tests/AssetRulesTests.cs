using Nid.Assets.Contracts;
using Nid.Assets.Domain;
using NodaTime;

namespace Nid.Assets.Tests;

public class AssetRulesTests
{
    private static readonly LocalDate Today = new(2026, 11, 25);

    private static AssetDetails Metal(decimal? fineness, decimal? weight = 10m)
        => new("precious_metal", 1_000m, new(2024, 1, 1), null, weight, fineness);

    private static void Refused(AssetDetails d, string code, string field)
    {
        var f = AssetRules.Check(d, Today);
        Assert.NotNull(f);
        Assert.Equal(code, f.Code);
        Assert.Equal(field, f.Metadata["field"]);
    }

    [Fact]
    public void Check_refuses_price_without_date() => Refused(new("art", 100m, null, null, null, null), AssetsCodes.PurchaseIncomplete, "purchasedOn");

    [Fact]
    public void Check_refuses_date_without_price() => Refused(new("art", null, new(2024, 1, 1), null, null, null), AssetsCodes.PurchaseIncomplete, "purchasePrice");

    [Fact]
    public void Check_refuses_weight_on_vehicle() => Refused(new("vehicle", null, null, null, 5m, null), AssetsCodes.MetalOnly, "weightGrams");

    [Fact]
    public void Check_accepts_fineness_999_9() => Assert.Null(AssetRules.Check(Metal(999.9m), Today));

    [Theory]
    [InlineData("0")]
    [InlineData("1000.1")]
    [InlineData("999.95")]
    public void Check_refuses_fineness_0_and_1000_1_and_two_decimals(string fineness)
        => Refused(Metal(decimal.Parse(fineness, System.Globalization.CultureInfo.InvariantCulture)), AssetsCodes.InvalidFineness, "fineness");

    [Fact]
    public void Check_refuses_zero_weight() => Refused(Metal(999m, 0m), AssetsCodes.InvalidWeight, "weightGrams");

    [Fact]
    public void Check_refuses_unknown_kind() => Refused(new("boat", null, null, null, null, null), AssetsCodes.UnknownKind, "kind");

    [Fact]
    public void Check_refuses_future_purchase_date() => Refused(new("art", 100m, Today.PlusDays(1), null, null, null), AssetsCodes.InvalidPurchase, "purchasedOn");

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("10.001")]
    [InlineData("1000000000000")]
    public void Check_refuses_invalid_price(string price)
        => Refused(new("art", decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture), new(2024, 1, 1), null, null, null), AssetsCodes.InvalidPurchase, "purchasePrice");

    [Fact]
    public void Check_refuses_weight_with_four_decimals() => Refused(Metal(999m, 1.0001m), AssetsCodes.InvalidWeight, "weightGrams");

    [Fact]
    public void Check_bounds_weight_to_numeric_12_3()
    {
        Assert.Null(AssetRules.Check(Metal(999m, 999_999_999.999m), Today));
        Refused(Metal(999m, 1_000_000_000m), AssetsCodes.InvalidWeight, "weightGrams");
    }

    [Fact]
    public void Check_refuses_fineness_on_art() => Refused(new("art", null, null, null, null, 999m), AssetsCodes.MetalOnly, "fineness");

    [Fact]
    public void HasValue_is_false_without_price_nor_valuation()
    {
        var d = new AssetDetails("other", null, null, null, null, null);
        Assert.False(AssetRules.HasValue(d, 0));
        Assert.True(AssetRules.HasValue(d, 1));
    }
}
