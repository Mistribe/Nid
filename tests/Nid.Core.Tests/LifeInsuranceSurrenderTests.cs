using Nid.Core.Domain.Tax;
using NodaTime;

namespace Nid.Core.Tests;

/// <summary>Sale tax spec §4.3, D3, D8d, D12, D13: life insurance contracts fully surrendered today (art. 125-0 A CGI).</summary>
public class LifeInsuranceSurrenderTests
{
    private static readonly LocalDate Today = new(2026, 10, 23);
    private static readonly SaleTaxRules Rules = SaleTaxRules.For(2026);
    private static readonly Guid Anne = Guid.NewGuid();
    private static readonly Guid Paul = Guid.NewGuid();

    // Remaining premiums are the same today and at 31 December unless a test says otherwise.
    private static LifeContract Contract(
        int years, decimal value, decimal before2017 = 0m, decimal after2017 = 0m, decimal? yearEndAfter2017 = null, SaleOwner[]? owners = null)
        => new(Guid.NewGuid(), Today.PlusYears(-years), value, before2017, after2017, before2017, yearEndAfter2017 ?? after2017,
            owners ?? [new SaleOwner(Anne, 10_000)]);

    private static ContractSaleTax One(LifeContract contract, decimal allowance)
        => Assert.Single(LifeInsuranceSurrender.Compute([contract], [contract], allowance, Today, Rules));

    [Fact]
    public void Under_eight_years_the_whole_gain_pays_the_flat_rate()
    {
        // gain 60 000 - 50 000 = 10 000: 12,8 % = 1 280, 17,2 % = 1 720, no allowance
        var r = One(Contract(7, 60_000m, after2017: 50_000m), Rules.LifeInsuranceAllowanceSingle);

        Assert.Equal((7, 10_000m, 0m, 0m, 10_000m, 0m), (r.YearsHeld, r.Gain, r.GainBefore2017, r.GainReduced, r.GainFull, r.AllowanceUsed));
        Assert.Equal((1_280m, 1_720m, 57_000m), (r.IncomeTax, r.SocialTax, r.Net));
    }

    [Fact]
    public void From_eight_years_a_single_person_has_4_600_allowance()
    {
        // gain 10 000, all post-2017 under the threshold: (10 000 - 4 600) x 7,5 % = 405
        var r = One(Contract(8, 60_000m, after2017: 50_000m), Rules.LifeInsuranceAllowanceSingle);

        Assert.Equal((8, 10_000m, 0m, 0m, 4_600m), (r.YearsHeld, r.GainReduced, r.GainFull, r.GainBefore2017, r.AllowanceUsed));
        Assert.Equal((405m, 1_720m, 57_875m), (r.IncomeTax, r.SocialTax, r.Net));
    }

    [Fact]
    public void From_eight_years_a_couple_has_9_200_allowance()
    {
        // (10 000 - 9 200) x 7,5 % = 60
        var r = One(Contract(8, 60_000m, after2017: 50_000m), Rules.LifeInsuranceAllowanceCouple);

        Assert.Equal((9_200m, 60m, 1_720m, 58_220m), (r.AllowanceUsed, r.IncomeTax, r.SocialTax, r.Net));
    }

    [Fact]
    public void A_gain_below_the_allowance_still_pays_social_levies()
    {
        // gain 3 000 < 4 600: no income tax, 17,2 % = 516
        var r = One(Contract(10, 53_000m, after2017: 50_000m), Rules.LifeInsuranceAllowanceSingle);

        Assert.Equal((3_000m, 0m, 516m), (r.AllowanceUsed, r.IncomeTax, r.SocialTax));
    }

    [Fact]
    public void The_threshold_counts_pre_2017_premiums()
    {
        // Anne holds 120 000 of pre-2017 premiums on one contract and 60 000 of post-2017 on the other at 31 December;
        // a payment of 10 000 this year does not count. 7,5 % on (150 000 - 120 000) / 60 000 = half the post-2017 gain.
        // old: gain 30 000 on pre-2017 premiums, 7,5 % = 2 250
        // recent: gain 90 000 - 70 000 = 20 000, 10 000 at 7,5 % + 10 000 at 12,8 % = 750 + 1 280 = 2 030
        var old = Contract(16, 150_000m, before2017: 120_000m);
        var recent = Contract(8, 90_000m, after2017: 70_000m, yearEndAfter2017: 60_000m);

        var r = LifeInsuranceSurrender.Compute([old, recent], [old, recent], 0m, Today, Rules);

        Assert.Equal((30_000m, 0m, 0m, 2_250m), (r[0].GainBefore2017, r[0].GainReduced, r[0].GainFull, r[0].IncomeTax));
        Assert.Equal((0m, 10_000m, 10_000m, 2_030m), (r[1].GainBefore2017, r[1].GainReduced, r[1].GainFull, r[1].IncomeTax));
    }

    [Fact]
    public void Pre_2017_premiums_above_the_threshold_leave_no_room_for_later_ones()
    {
        // 200 000 of pre-2017 premiums and none after at 31 December: the 50 000 paid this year is over the threshold.
        // gain 25 000: 20 000 on pre-2017 premiums at 7,5 % = 1 500, 5 000 at 12,8 % = 640
        var r = One(Contract(10, 275_000m, before2017: 200_000m, after2017: 50_000m, yearEndAfter2017: 0m), 0m);

        Assert.Equal((20_000m, 0m, 5_000m, 2_140m), (r.GainBefore2017, r.GainReduced, r.GainFull, r.IncomeTax));
    }

    [Fact]
    public void A_contract_without_premiums_has_its_whole_gain_after_2017()
    {
        var r = One(Contract(9, 1_000m), 0m);

        Assert.Equal((0m, 1_000m, 0m), (r.GainBefore2017, r.GainReduced, r.GainFull));
    }

    [Fact]
    public void Each_holder_has_their_own_threshold()
    {
        // 200 000 of premiums held 50/50: 100 000 each, under each holder's 150 000, so the whole gain of 40 000 is at 7,5 %
        // = 3 000 (one holder of 200 000 would have had 150 000 / 200 000 = 30 000 at 7,5 % and 10 000 at 12,8 %).
        var shared = Contract(9, 240_000m, after2017: 200_000m, owners: [new SaleOwner(Anne, 5_000), new SaleOwner(Paul, 5_000)]);

        var r = One(shared, 0m);

        Assert.Equal((40_000m, 0m, 3_000m), (r.GainReduced, r.GainFull, r.IncomeTax));
    }

    [Fact]
    public void The_allowance_goes_to_pre_2017_products_first()
    {
        // Anne: old has 100 000 of pre-2017 premiums and a gain of 4 000; recent has 100 000 of post-2017 premiums and a gain
        // of 10 000, half under the threshold ((150 000 - 100 000) / 100 000): 5 000 at 7,5 %, 5 000 at 12,8 %.
        // Legal order of 9 200: 4 000 on the pre-2017 part, 5 000 on the 7,5 % part, 200 on the 12,8 % part
        //   -> 4 800 x 12,8 % = 614,40 -> 614.
        // An even split (9 200 / 14 000 of each part) would leave 1 371,43 + 1 714,29 at 7,5 % and 1 714,29 at 12,8 %
        //   -> 231,43 + 219,43 = 450,86: the order the law requires costs more here, no saving.
        var old = Contract(16, 104_000m, before2017: 100_000m);
        var recent = Contract(8, 110_000m, after2017: 100_000m);

        var r = LifeInsuranceSurrender.Compute([old, recent], [old, recent], Rules.LifeInsuranceAllowanceCouple, Today, Rules);

        Assert.Equal((4_000m, 0m), (r[0].AllowanceUsed, r[0].IncomeTax));
        Assert.Equal((5_000m, 5_000m, 5_200m, 614m), (r[1].GainReduced, r[1].GainFull, r[1].AllowanceUsed, r[1].IncomeTax));
    }

    [Fact]
    public void Two_contracts_share_one_allowance_pro_rata_of_their_gain()
    {
        // gains 6 000 and 3 000 at 7,5 %: 4 600 split 3 066,67 / 1 533,33 -> taxes 2 933,33 x 7,5 % = 220 and 1 466,67 x 7,5 % = 110
        var a = Contract(9, 56_000m, after2017: 50_000m);
        var b = Contract(12, 28_000m, after2017: 25_000m);

        var r = LifeInsuranceSurrender.Compute([a, b], [a, b], Rules.LifeInsuranceAllowanceSingle, Today, Rules);

        Assert.Equal((3_067m, 220m), (r[0].AllowanceUsed, r[0].IncomeTax));
        Assert.Equal((1_533m, 110m), (r[1].AllowanceUsed, r[1].IncomeTax));
    }

    [Fact]
    public void Rounded_allowance_shares_sum_to_the_allowance()
    {
        // 4 600 / 3 = 1 533,33 each: rounded alone they would sum to 4 599
        var contracts = Enumerable.Range(0, 3).Select(_ => Contract(9, 33_000m, after2017: 30_000m)).ToList();

        var r = LifeInsuranceSurrender.Compute(contracts, contracts, Rules.LifeInsuranceAllowanceSingle, Today, Rules);

        Assert.Equal([1_534m, 1_533m, 1_533m], r.Select(c => c.AllowanceUsed));
    }

    [Fact]
    public void A_fiche_takes_the_whole_allowance_and_the_holders_full_threshold()
    {
        // Only recent is taxed, but old's 120 000 of pre-2017 premiums still count: 10 000 at 7,5 %, 10 000 at 12,8 %.
        // The whole 4 600 goes to the 7,5 % part: 5 400 x 7,5 % + 10 000 x 12,8 % = 405 + 1 280 = 1 685
        var old = Contract(16, 150_000m, before2017: 120_000m);
        var recent = Contract(8, 80_000m, after2017: 60_000m);

        var r = Assert.Single(LifeInsuranceSurrender.Compute([recent], [old, recent], Rules.LifeInsuranceAllowanceSingle, Today, Rules));

        Assert.Equal((recent.Item, 10_000m, 10_000m, 4_600m, 1_685m), (r.Item, r.GainReduced, r.GainFull, r.AllowanceUsed, r.IncomeTax));
    }

    [Fact]
    public void A_loss_pays_nothing()
    {
        var r = One(Contract(10, 45_000m, after2017: 50_000m), Rules.LifeInsuranceAllowanceSingle);

        Assert.Equal((-5_000m, 0m, 0m, 0m, 0m), (r.Gain, r.GainBefore2017, r.GainReduced, r.GainFull, r.AllowanceUsed));
        Assert.Equal((0m, 0m, 45_000m), (r.IncomeTax, r.SocialTax, r.Net));
    }
}
