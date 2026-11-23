using Nid.Tax.Domain;

namespace Nid.Tax.Tests;

/// <summary>Issue #235: one scale per revenue year, the latest carried forward.</summary>
public class TaxRulesTests
{
    /// <summary>
    /// The day each revenue year's finance law is out, or due by (a law late in February included). Past that day the year must
    /// have its own scale from the law: add it to <c>TaxRules</c>, then the next year here.
    /// </summary>
    private static readonly Dictionary<int, DateOnly> Laws = new()
    {
        [2020] = new(2020, 12, 30), // loi n° 2020-1721 du 29 décembre 2020, JO du 30
        [2021] = new(2021, 12, 31), // loi n° 2021-1900 du 30 décembre 2021, JO du 31
        [2022] = new(2022, 12, 31), // loi n° 2022-1726 du 30 décembre 2022, JO du 31
        [2023] = new(2023, 12, 30), // loi n° 2023-1322 du 29 décembre 2023, JO du 30
        [2024] = new(2025, 2, 15), // loi n° 2025-127 du 14 février 2025, JO du 15
        [2025] = new(2026, 2, 20), // loi n° 2026-103 du 19 février 2026, JO du 20
        [2026] = new(2027, 3, 1), // loi de finances pour 2027, due
        [2027] = new(2028, 3, 1), // loi de finances pour 2028, due
    };

    [Fact]
    public void A_year_whose_finance_law_is_out_has_its_own_final_scale()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        Assert.Contains(today.Year, Laws.Keys);
        foreach (var (year, _) in Laws.Where(l => l.Value <= today))
        {
            var rules = TaxRules.For(year);
            Assert.False(rules.CarriedForward, $"The revenues of {year} run on the {rules.RulesYear} scale.");
            Assert.False(rules.Provisional, $"The revenues of {year} still run on the bill's scale.");
        }
    }

    [Fact]
    public void The_2026_revenues_have_the_bills_scale()
    {
        var rules = TaxRules.For(2026);

        Assert.Equal((2026, false, true), (rules.RulesYear, rules.CarriedForward, rules.Provisional));
        Assert.Equal([0m, 11_844m, 30_200m, 86_353m, 185_737m], rules.Brackets.Select(b => b.From));
        Assert.Equal((520m, 14_861m, 1_845m), (rules.SalaryAllowanceMin, rules.SalaryAllowanceMax, rules.FamilyCapPerHalfShare));
        Assert.Equal((915m, 1_513m), (rules.DecoteSingleBase, rules.DecoteCoupleBase));
    }

    [Fact]
    public void A_later_year_carries_the_latest_scale_forward()
    {
        var rules = TaxRules.For(TaxRules.LatestYear + 1);

        Assert.Equal((TaxRules.LatestYear, true), (rules.RulesYear, rules.CarriedForward));
        Assert.Equal(TaxRules.For(TaxRules.LatestYear).Brackets, rules.Brackets);
    }

    [Theory]
    [InlineData(2019, 40_524)] // the 2020 PER ceiling's
    [InlineData(2020, 41_136)]
    [InlineData(2022, 41_136)]
    [InlineData(2023, 43_992)]
    [InlineData(2024, 46_368)]
    [InlineData(2025, 47_100)]
    [InlineData(2026, 48_060)] // arrêté du 22 décembre 2025
    [InlineData(2027, 48_060)] // carried forward
    public void The_pass_of_a_year(int year, int pass) => Assert.Equal(pass, TaxRules.Pass(year));

    /// <summary>The first bracket, the family cap per half share and the couple's décote base of each past year (CGI art. 197, OpenFisca France).</summary>
    [Theory]
    [InlineData(2020, 10_084, 1_570, 1_289)]
    [InlineData(2021, 10_225, 1_592, 1_307)]
    [InlineData(2022, 10_777, 1_678, 1_378)]
    [InlineData(2023, 11_294, 1_759, 1_444)]
    [InlineData(2024, 11_497, 1_791, 1_470)]
    public void The_scale_of_a_past_year(int year, int firstBracket, int familyCap, int decoteCoupleBase)
    {
        var rules = TaxRules.For(year);

        Assert.Equal((year, false, false), (rules.RulesYear, rules.CarriedForward, rules.Provisional));
        Assert.Equal((firstBracket, familyCap, decoteCoupleBase), ((int)rules.Brackets[1].From, (int)rules.FamilyCapPerHalfShare, (int)rules.DecoteCoupleBase));
    }

    [Fact]
    public void The_revenues_before_2020_have_no_rules() => Assert.Throws<ArgumentOutOfRangeException>(() => TaxRules.For(2019));

    [Fact]
    public void The_energy_deficit_cap_starts_with_the_2023_revenues()
        => Assert.Equal(((decimal?)null, (decimal?)21_400m, (decimal?)21_400m, (decimal?)null),
            (TaxRules.For(2022).RentalDeficitEnergyCap, TaxRules.For(2023).RentalDeficitEnergyCap, TaxRules.For(2025).RentalDeficitEnergyCap, TaxRules.For(2026).RentalDeficitEnergyCap));

    [Fact]
    public void The_aid_donations_cap_before_2025_is_1000()
        => Assert.Equal((1_000m, (decimal?)null, 2_000m), (TaxRules.For(2024).DonationAidCap, TaxRules.For(2024).DonationAidEarlyCap, TaxRules.For(2025).DonationAidCap));

    [Fact]
    public void The_childcare_cap_rose_with_the_2022_revenues()
        => Assert.Equal((2_300m, 3_500m), (TaxRules.For(2021).ChildcareCapPerChild, TaxRules.For(2022).ChildcareCapPerChild));
}
