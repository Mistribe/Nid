using Nid.Core.Domain.Tax;
using NodaTime;

namespace Nid.Core.Tests;

/// <summary>Sale tax spec §3, D4, D5, D8c, D15: the capital gain on a property sold today, per owner.</summary>
public class PropertySaleTests
{
    private static readonly LocalDate Today = new(2026, 10, 23);
    private static readonly Guid Alice = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Bob = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly SaleTaxRules Rules = SaleTaxRules.For(2026);

    private static PropertySaleResult Sale(
        PropertySaleKind kind = PropertySaleKind.Other, LocalDate? purchasedOn = null, decimal fees = 0m, decimal works = 0m,
        decimal salePrice = 300_000m, params SaleOwner[] owners) =>
        PropertySale.Compute(new PropertySaleInput(
            kind, purchasedOn ?? Today.PlusYears(-3), 200_000m, fees, works, salePrice,
            owners.Length == 0 ? [new SaleOwner(Alice, 10_000)] : owners, Today), Rules);

    [Fact]
    public void A_primary_residence_is_exempt()
    {
        var r = Sale(PropertySaleKind.PrimaryResidence, Today.PlusYears(-10), salePrice: 500_000m);

        Assert.True(r.Exempt);
        Assert.Equal((0m, 0m, 0m), (r.IncomeTax, r.SocialTax, r.Surtax));
        Assert.Equal(500_000m, r.Net);
    }

    // Worked example: bought 2010-06-15 for 200 000, sold today (2026-10-23) for 345 000, one owner at 100 %.
    //   years held 16; fees real 9 000 < flat 7,5 % x 200 000 = 15 000 -> flat 15 000; works real 20 000 < flat 15 % = 30 000 -> flat 30 000
    //   cost price 200 000 + 15 000 + 30 000 = 245 000; gain 345 000 - 245 000 = 100 000
    //   income allowance (16 - 5) x 6 % = 66 % -> base 34 000, tax 19 % = 6 460; surtax(34 000) = 0
    //   social allowance 11 x 1,65 % = 18,15 % -> base 81 850, tax 17,2 % = 14 078,2 -> 14 078
    //   net 345 000 - 6 460 - 14 078 - 0 = 324 462
    [Fact]
    public void Worked_example()
    {
        var r = Sale(purchasedOn: new LocalDate(2010, 6, 15), fees: 9_000m, works: 20_000m, salePrice: 345_000m);

        Assert.False(r.Exempt);
        Assert.Equal(16, r.YearsHeld);
        Assert.Equal(("flat", "flat"), (r.FeesMethod, r.WorksMethod));
        Assert.Equal(245_000m, r.CostPrice);
        Assert.Equal(100_000m, r.Gain);
        var o = Assert.Single(r.Owners);
        Assert.Equal((0.66m, 0.1815m), (o.IncomeAllowance, o.SocialAllowance));
        Assert.Equal((34_000m, 81_850m), (o.IncomeBase, o.SocialBase));
        Assert.Equal((6_460m, 14_078m, 0m), (r.IncomeTax, r.SocialTax, r.Surtax));
        Assert.Equal(324_462m, r.Net);
    }

    [Fact]
    public void Fees_are_the_more_favourable_of_real_and_flat()
    {
        var flat = Sale(fees: 9_000m);
        var actual = Sale(fees: 20_000m);

        Assert.Equal("flat", flat.FeesMethod);
        Assert.Equal(215_000m, flat.CostPrice);
        Assert.Equal("actual", actual.FeesMethod);
        Assert.Equal(220_000m, actual.CostPrice);
    }

    [Fact]
    public void Works_before_5_years_are_real_only()
    {
        var r = Sale(works: 10_000m);

        Assert.Equal("actual", r.WorksMethod);
        Assert.Equal(200_000m + 15_000m + 10_000m, r.CostPrice);
        Assert.Equal("none", Sale().WorksMethod);
    }

    [Fact]
    public void Works_after_5_years_are_the_more_favourable_of_real_and_flat()
    {
        var past = new LocalDate(2020, 1, 1);

        Assert.Equal("flat", Sale(purchasedOn: past, works: 10_000m).WorksMethod);
        var actual = Sale(purchasedOn: past, works: 40_000m);
        Assert.Equal("actual", actual.WorksMethod);
        Assert.Equal(200_000m + 15_000m + 40_000m, actual.CostPrice);
    }

    [Fact]
    public void The_works_flat_needs_more_than_5_years_not_6_full_years()
    {
        // art. 150 VB II 4° CGI: « au-delà de cinq ans »: the 5th anniversary itself does not qualify, the next day does.
        var onAnniversary = PropertySale.Compute(new PropertySaleInput(
            PropertySaleKind.Other, Today.PlusYears(-5), 200_000m, 0m, 0m, 300_000m, [new SaleOwner(Alice, 10_000)], Today), Rules);
        var dayAfter = PropertySale.Compute(new PropertySaleInput(
            PropertySaleKind.Other, Today.PlusYears(-5).PlusDays(-1), 200_000m, 0m, 0m, 300_000m, [new SaleOwner(Alice, 10_000)], Today), Rules);

        Assert.Equal("none", onAnniversary.WorksMethod);
        Assert.Equal("flat", dayAfter.WorksMethod);
        Assert.Equal(245_000m, dayAfter.CostPrice);
    }

    [Fact]
    public void A_rental_takes_the_works_flat_only_and_ignores_real_works()
    {
        var after = Sale(PropertySaleKind.Rental, Today.PlusYears(-8), works: 50_000m);
        var before = Sale(PropertySaleKind.Rental, Today.PlusYears(-3), works: 50_000m);

        Assert.Equal("flat", after.WorksMethod);
        Assert.Equal(200_000m + 15_000m + 30_000m, after.CostPrice);
        Assert.Contains("rental_works_flat_only", after.Notes);
        Assert.Equal("none", before.WorksMethod);
        Assert.Equal(215_000m, before.CostPrice);
    }

    [Fact]
    public void Land_has_no_surtax_and_no_works_flat()
    {
        // 8 years held: real works 5 000 count, no 15 % flat.
        var r = Sale(PropertySaleKind.Land, Today.PlusYears(-8), works: 5_000m, salePrice: 400_000m);

        Assert.Equal("actual", r.WorksMethod);
        Assert.Equal(200_000m + 15_000m + 5_000m, r.CostPrice);
        Assert.Equal(0m, r.Surtax);
        Assert.Contains("land_surtax_not_applied", r.Notes);
        Assert.Equal("none", Sale(PropertySaleKind.Land, Today.PlusYears(-8)).WorksMethod);
    }

    [Fact]
    public void Allowances_at_the_boundaries()
    {
        // fees flat, no works: cost 215 000 + works flat 30 000 = 245 000, sale 300 000 -> gain 55 000
        var at22 = Sale(purchasedOn: Today.PlusYears(-22), salePrice: 300_000m);
        var at30 = Sale(purchasedOn: Today.PlusYears(-30), salePrice: 300_000m);
        var at21 = Sale(purchasedOn: Today.PlusYears(-21), salePrice: 300_000m);

        Assert.Equal(0m, at22.IncomeTax);
        Assert.Equal(0.28m, at22.Owners[0].SocialAllowance);
        Assert.Equal(6_811m, at22.SocialTax); // 55 000 x 0,72 x 17,2 % = 6 811,2
        Assert.Equal((0m, 0m), (at30.IncomeTax, at30.SocialTax));
        Assert.True(at21.IncomeTax > 0m);
    }

    [Fact]
    public void Surtax_is_per_owner()
    {
        // 200 000 + fees 20 000 = 220 000, sale 300 000 -> gain 80 000, held 3 years: no allowance
        var shared = Sale(fees: 20_000m, owners: [new SaleOwner(Alice, 5_000), new SaleOwner(Bob, 5_000)]);
        var alone = Sale(fees: 20_000m);

        Assert.Equal(0m, shared.Surtax);
        Assert.Equal(1_600m, alone.Surtax);
        Assert.Equal(alone.IncomeTax, shared.IncomeTax);
    }

    [Fact]
    public void Owners_shares_add_up_to_the_gain()
    {
        // gain 100 000 / 3 shares: the last takes the remainder
        var r = Sale(fees: 20_000m, salePrice: 320_000m,
            owners: [new SaleOwner(Alice, 3_333), new SaleOwner(Bob, 3_333), new SaleOwner(Guid.NewGuid(), 3_334)]);

        Assert.Equal(r.Gain, r.Owners.Sum(o => o.Gain));
        Assert.Equal(33_330m, r.Owners[0].Gain);
        Assert.Equal(33_340m, r.Owners[2].Gain);
        Assert.Equal(r.IncomeTax, r.Owners.Sum(o => o.IncomeTax));
    }

    [Fact]
    public void Owners_rounded_gains_sum_to_the_rounded_gain()
    {
        // gain 100 001 / 2: 50 000,50 each, the euro left goes to the first
        var r = Sale(fees: 20_000m, salePrice: 320_001m, owners: [new SaleOwner(Alice, 5_000), new SaleOwner(Bob, 5_000)]);

        Assert.Equal(100_001m, r.Gain);
        Assert.Equal(new[] { 50_001m, 50_000m }, r.Owners.Select(o => o.Gain));
    }

    [Fact]
    public void A_loss_is_not_taxed_and_the_net_is_the_sale_price()
    {
        var r = Sale(salePrice: 150_000m);

        Assert.True(r.Gain < 0m);
        Assert.Equal((0m, 0m, 0m), (r.IncomeTax, r.SocialTax, r.Surtax));
        Assert.Equal(150_000m, r.Net);
        Assert.Contains("sale_costs_not_deducted", r.Notes);
    }
}
