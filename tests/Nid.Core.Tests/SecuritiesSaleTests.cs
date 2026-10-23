using Nid.Core.Domain.Tax;
using NodaTime;

namespace Nid.Core.Tests;

/// <summary>Sale tax spec §4.2, §4.5, D6, D14, D17: PEA, brokerage account and the netting of same-kind gains.</summary>
public class SecuritiesSaleTests
{
    private static readonly LocalDate Today = new(2026, 10, 23);
    private static readonly SaleTaxRules Rules = SaleTaxRules.For(2026);

    [Fact]
    public void A_pea_under_five_years_pays_both_taxes()
    {
        // gain 12 000 - 10 000 = 2 000: 12,8 % = 256, 18,6 % = 372, net 12 000 - 628
        var r = SecuritiesSale.Pea(Today.PlusYears(-4), 12_000m, 10_000m, false, Today, Rules);

        Assert.Equal((2_000m, 256m, 372m, 11_372m, false), (r.Gain, r.IncomeTax, r.SocialTax, r.Net, r.Approximate));
        Assert.Equal(["pea_closes_under_five_years"], r.Notes);
    }

    [Fact]
    public void A_pea_of_exactly_five_years_pays_social_levies_only()
    {
        var r = SecuritiesSale.Pea(Today.PlusYears(-5), 12_000m, 10_000m, true, Today, Rules);

        Assert.Equal((0m, 372m, 11_628m, true), (r.IncomeTax, r.SocialTax, r.Net, r.Approximate));
        Assert.Empty(r.Notes);
    }

    [Fact]
    public void A_pea_loss_pays_nothing()
    {
        var r = SecuritiesSale.Pea(Today.PlusYears(-2), 9_000m, 10_000m, false, Today, Rules);

        Assert.Equal((-1_000m, 0m, 0m, 9_000m), (r.Gain, r.IncomeTax, r.SocialTax, r.Net));
    }

    [Fact]
    public void A_pea_opened_before_2018_is_noted()
    {
        var r = SecuritiesSale.Pea(new LocalDate(2016, 5, 1), 12_000m, 10_000m, false, Today, Rules);

        Assert.Equal(["pea_pre_2018_rates_not_modelled"], r.Notes);
    }

    [Fact]
    public void The_social_rate_follows_the_revenue_year()
    {
        var in2025 = SecuritiesSale.Pea(Today.PlusYears(-6), 12_000m, 10_000m, false, Today, SaleTaxRules.For(2025));

        Assert.Equal(344m, in2025.SocialTax); // 17,2 %
        Assert.Equal(372m, SecuritiesSale.Pea(Today.PlusYears(-6), 12_000m, 10_000m, false, Today, Rules).SocialTax); // 18,6 %
    }

    [Fact]
    public void A_brokerage_with_lines_sums_the_gain_of_each_line()
    {
        // (6 000 - 10 x 400) + (4 000 - 20 x 150) = 2 000 + 1 000 = 3 000: 384 + 558
        var lines = new PricedLine[] { new(6_000m, 10m, 400m), new(4_000m, 20m, 150m) };
        var r = SecuritiesSale.Brokerage(10_000m, lines, 9_000m, Rules);

        Assert.Equal((3_000m, 384m, 558m, 9_058m, false), (r.Gain, r.IncomeTax, r.SocialTax, r.Net, r.Approximate));
        Assert.Equal(["brokerage_scale_option"], r.Notes);
    }

    [Fact]
    public void A_brokerage_without_lines_uses_net_contributions_and_is_approximate()
    {
        var r = SecuritiesSale.Brokerage(10_000m, null, 9_000m, Rules);

        Assert.Equal((1_000m, 128m, 186m, true), (r.Gain, r.IncomeTax, r.SocialTax, r.Approximate));
    }

    [Fact]
    public void A_brokerage_loss_pays_nothing()
    {
        var r = SecuritiesSale.Brokerage(8_000m, [], 9_000m, Rules);

        Assert.Equal((-1_000m, 0m, 0m, 8_000m), (r.Gain, r.IncomeTax, r.SocialTax, r.Net));
    }

    [Fact]
    public void A_brokerage_loss_offsets_a_pea_gain_for_income_tax_only()
    {
        // income tax on -3 000 + 10 000 = 7 000: 896, all on the PEA; its social levies stay on its own 10 000: 1 860
        var brokerage = Guid.NewGuid();
        var pea = Guid.NewGuid();
        var taxes = SecuritiesSale.Netted([new(brokerage, -3_000m, false), new(pea, 10_000m, true)], Rules);

        Assert.Equal((0m, 0m), taxes[brokerage]);
        Assert.Equal((896m, 1_860m), taxes[pea]);
    }

    [Fact]
    public void A_pea_loss_offsets_brokerage_gains_for_income_tax_only()
    {
        // income tax on 10 000 - 4 000 = 6 000: 768; social levies on the brokerage's own 10 000: 1 860; none on the PEA's loss
        var brokerage = Guid.NewGuid();
        var pea = Guid.NewGuid();
        var taxes = SecuritiesSale.Netted([new(brokerage, 10_000m, false), new(pea, -4_000m, true)], Rules);

        Assert.Equal((768m, 1_860m), taxes[brokerage]);
        Assert.Equal((0m, 0m), taxes[pea]);
    }

    [Fact]
    public void Brokerage_losses_offset_brokerage_gains_for_both_taxes()
    {
        // 10 000 - 4 000 = 6 000: 768 + 1 116
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var taxes = SecuritiesSale.Netted([new(a, 10_000m, false), new(b, -4_000m, false)], Rules);

        Assert.Equal((768m, 1_116m), taxes[a]);
        Assert.Equal((0m, 0m), taxes[b]);
    }

    [Fact]
    public void Netted_tax_is_spread_pro_rata_and_sums_to_the_rounded_total()
    {
        // net 3 x 100,10 = 300,30: 12,8 % = 38,4384 -> 38, 18,6 % = 55,8558 -> 56; 12,81 and 18,62 each, the euros left to the first
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var taxes = SecuritiesSale.Netted([new(a, 100.10m, false), new(b, 100.10m, false), new(c, 100.10m, false)], Rules);

        Assert.Equal(38m, taxes.Values.Sum(t => t.IncomeTax));
        Assert.Equal(56m, taxes.Values.Sum(t => t.SocialTax));
        Assert.Equal((13m, 19m), taxes[a]);
        Assert.Equal((12m, 18m), taxes[c]);
    }

    [Fact]
    public void Netted_tax_follows_unequal_gains()
    {
        // net 6 000 + 2 000 - 1 000 = 7 000: income 896 = 672 + 224; social 1 302 = 976,50 + 325,50 -> 977 + 325
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var taxes = SecuritiesSale.Netted([new(a, 6_000m, false), new(b, 2_000m, false), new(c, -1_000m, false)], Rules);

        Assert.Equal((672m, 977m), taxes[a]);
        Assert.Equal((224m, 325m), taxes[b]);
        Assert.Equal((0m, 0m), taxes[c]);
    }

    [Fact]
    public void A_tiny_netted_tax_never_gives_a_negative_share()
    {
        // net 16: income 2,048 -> 2 and social 2,976 -> 3, over four equal gains of 0,512 and 0,744
        var items = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();
        var taxes = SecuritiesSale.Netted(items.Select(i => new NettableGain(i, 4m, false)).ToList(), Rules);

        Assert.All(taxes.Values, t => Assert.True(t.IncomeTax >= 0m && t.SocialTax >= 0m));
        Assert.Equal(new[] { 1m, 1m, 0m, 0m }, items.Select(i => taxes[i].IncomeTax));
        Assert.Equal(new[] { 1m, 1m, 1m, 0m }, items.Select(i => taxes[i].SocialTax));
    }

    [Fact]
    public void A_net_loss_pays_nothing()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var taxes = SecuritiesSale.Netted([new(a, 2_000m, false), new(b, -5_000m, false)], Rules);

        Assert.All(taxes.Values, t => Assert.Equal((0m, 0m), t));
    }
}
