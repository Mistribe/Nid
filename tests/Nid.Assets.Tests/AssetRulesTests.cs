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

    private static AssetDetails Car(LocalDate? registeredOn = null, int? km = null, LocalDate? kmOn = null, int? hp = null, string? energy = null)
        => new("vehicle", null, null, null, null, null, registeredOn, km, kmOn, hp, energy);

    [Fact]
    public void Check_accepts_a_full_vehicle() => Assert.Null(AssetRules.Check(Car(new(2020, 5, 1), 45_000, new(2026, 11, 1), 5, "hybrid"), Today));

    [Theory]
    [InlineData("registeredOn")]
    [InlineData("mileageKm")]
    [InlineData("mileageOn")]
    [InlineData("fiscalHp")]
    [InlineData("energy")]
    public void Check_refuses_a_vehicle_field_on_art(string field)
    {
        var d = field switch
        {
            "registeredOn" => new AssetDetails("art", null, null, null, null, null, new LocalDate(2020, 1, 1)),
            "mileageKm" => new AssetDetails("art", null, null, null, null, null, MileageKm: 10),
            "mileageOn" => new AssetDetails("art", null, null, null, null, null, MileageOn: new LocalDate(2020, 1, 1)),
            "fiscalHp" => new AssetDetails("art", null, null, null, null, null, FiscalHp: 5),
            _ => new AssetDetails("art", null, null, null, null, null, Energy: "diesel"),
        };
        Refused(d, AssetsCodes.VehicleOnly, field);
    }

    [Fact]
    public void Check_refuses_mileage_without_its_date() => Refused(Car(km: 100), AssetsCodes.MileageIncomplete, "mileageOn");

    [Fact]
    public void Check_refuses_mileage_date_without_mileage() => Refused(Car(kmOn: new(2026, 1, 1)), AssetsCodes.MileageIncomplete, "mileageKm");

    [Theory]
    [InlineData(-1)]
    [InlineData(10_000_000)]
    public void Check_refuses_mileage_out_of_range(int km) => Refused(Car(km: km, kmOn: new(2026, 1, 1)), AssetsCodes.InvalidMileage, "mileageKm");

    [Fact]
    public void Check_accepts_mileage_zero_and_the_bound()
    {
        Assert.Null(AssetRules.Check(Car(km: 0, kmOn: new(2026, 1, 1)), Today));
        Assert.Null(AssetRules.Check(Car(km: 9_999_999, kmOn: new(2026, 1, 1)), Today));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void Check_refuses_fiscal_hp_out_of_range(int hp) => Refused(Car(hp: hp), AssetsCodes.InvalidFiscalHp, "fiscalHp");

    [Fact]
    public void Check_refuses_unknown_energy() => Refused(Car(energy: "steam"), AssetsCodes.InvalidEnergy, "energy");

    [Fact]
    public void Check_refuses_a_future_registration() => Refused(Car(new(2026, 11, 26)), AssetsCodes.InvalidDate, "registeredOn");

    [Fact]
    public void Check_refuses_a_future_mileage_date() => Refused(Car(km: 1, kmOn: new(2026, 11, 26)), AssetsCodes.InvalidDate, "mileageOn");

    [Fact]
    public void Check_accepts_today_for_both_dates() => Assert.Null(AssetRules.Check(Car(Today, 1, Today), Today));

    [Theory]
    [InlineData("0")]
    [InlineData("100")]
    [InlineData("-5")]
    [InlineData("150")]
    [InlineData("12.345")]
    public void Check_refuses_a_depreciation_rate_outside_zero_to_one_hundred(string rate)
        => Refused(new AssetDetails("vehicle", null, null, null, null, null, DepreciationRate: decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture)), AssetsCodes.InvalidDepreciationRate, "depreciationRate");

    [Theory]
    [InlineData("0.01")]
    [InlineData("15")]
    [InlineData("99.99")]
    public void Check_accepts_a_depreciation_rate_on_a_vehicle(string rate)
        => Assert.Null(AssetRules.Check(new AssetDetails("vehicle", null, null, null, null, null, DepreciationRate: decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture)), new(2026, 6, 1)));

    [Fact]
    public void Check_refuses_a_depreciation_rate_on_art()
        => Refused(new AssetDetails("art", null, null, null, null, null, DepreciationRate: 10m), AssetsCodes.VehicleOnly, "depreciationRate");
}
