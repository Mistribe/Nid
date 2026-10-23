using Nid.Core.Domain.Tax;
using NodaTime;

namespace Nid.Core.Tests;

/// <summary>Sale tax rules: the rates of the year, the property allowances, the surtax and the full years held.</summary>
public class SaleTaxRulesTests
{
    [Fact]
    public void The_capital_social_rate_follows_the_LFSS_while_property_and_life_insurance_stay_at_17_2()
    {
        var r2025 = TaxRules.For(2025).Sale;
        var r2026 = TaxRules.For(2026).Sale;

        Assert.Equal(0.172m, r2025.CapitalSocialRate);
        Assert.Equal(0.186m, r2026.CapitalSocialRate);
        foreach (var r in new[] { r2025, r2026 })
        {
            Assert.Equal(0.128m, r.FlatTaxRate);
            Assert.Equal(0.172m, r.PropertySocialRate);
            Assert.Equal(0.172m, r.LifeInsuranceSocialRate);
            Assert.Equal(0.19m, r.PropertyIncomeRate);
            Assert.Equal(0.075m, r.LifeInsuranceReducedRate);
            Assert.Equal(4_600m, r.LifeInsuranceAllowanceSingle);
            Assert.Equal(9_200m, r.LifeInsuranceAllowanceCouple);
            Assert.Equal(150_000m, r.LifeInsurancePremiumThreshold);
            Assert.Equal(0.075m, r.AcquisitionFlatRate);
            Assert.Equal(0.15m, r.WorksFlatRate);
        }
    }

    [Theory]
    [InlineData(2025, 2025, false)]
    [InlineData(2026, 2026, false)]
    [InlineData(2027, 2026, true)]
    public void Rules_after_2026_are_carried_forward(int revenueYear, int rulesYear, bool carriedForward)
    {
        var r = SaleTaxRules.For(revenueYear);

        Assert.Equal((rulesYear, carriedForward), (r.RulesYear, r.CarriedForward));
    }

    [Theory]
    [InlineData(5, 0)]
    [InlineData(6, 0.06)]
    [InlineData(21, 0.96)]
    [InlineData(22, 1.00)]
    [InlineData(23, 1.00)]
    public void Income_allowance_by_years_held(int years, double expected) =>
        Assert.Equal((decimal)expected, PropertyAllowance.Income(years));

    [Theory]
    [InlineData(5, 0)]
    [InlineData(6, 0.0165)]
    [InlineData(21, 0.264)]
    [InlineData(22, 0.28)]
    [InlineData(23, 0.37)]
    [InlineData(30, 1.00)]
    [InlineData(31, 1.00)]
    public void Social_allowance_by_years_held(int years, double expected) =>
        Assert.Equal((decimal)expected, PropertyAllowance.Social(years));

    [Theory]
    [InlineData(50_000, 0)]
    [InlineData(55_000, 850)]
    [InlineData(60_000, 1_200)]
    [InlineData(105_000, 2_650)]
    [InlineData(155_000, 5_450)]
    [InlineData(205_000, 9_250)]
    [InlineData(255_000, 14_050)]
    [InlineData(260_000, 15_600)]
    [InlineData(300_000, 18_000)]
    public void Surtax_follows_the_smoothed_scale(int gain, int expected) =>
        Assert.Equal((decimal)expected, Surtax.Of(gain));

    [Fact]
    public void Full_years_held_count_anniversaries()
    {
        Assert.Equal(4, HeldYears.FullYears(new LocalDate(2020, 6, 15), new LocalDate(2025, 6, 14)));
        Assert.Equal(5, HeldYears.FullYears(new LocalDate(2020, 6, 15), new LocalDate(2025, 6, 15)));
        Assert.Equal(0, HeldYears.FullYears(new LocalDate(2020, 6, 15), new LocalDate(2020, 6, 15)));
    }

    [Fact]
    public void A_29_February_start_has_its_anniversary_on_28_February_in_common_years()
    {
        var from = new LocalDate(2020, 2, 29);
        Assert.Equal(0, HeldYears.FullYears(from, new LocalDate(2021, 2, 27)));
        Assert.Equal(1, HeldYears.FullYears(from, new LocalDate(2021, 2, 28)));
        Assert.Equal(4, HeldYears.FullYears(from, new LocalDate(2024, 2, 29)));
    }
}
