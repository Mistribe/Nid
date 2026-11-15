using Nid.Core.Domain.Tax;

namespace Nid.Core.Tests;

/// <summary>Issue #235: one scale per revenue year, the latest carried forward.</summary>
public class TaxRulesTests
{
    /// <summary>
    /// The day each revenue year's finance law is out, or due by (a law late in February included). Past that day the year must
    /// have its own scale from the law: add it to <c>TaxRules</c>, then the next year here.
    /// </summary>
    private static readonly Dictionary<int, DateOnly> Laws = new()
    {
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
    [InlineData(2023, 46_368)]
    [InlineData(2025, 47_100)]
    [InlineData(2026, 48_060)] // arrêté du 22 décembre 2025
    [InlineData(2027, 48_060)] // carried forward
    public void The_pass_of_a_year(int year, int pass) => Assert.Equal(pass, TaxRules.Pass(year));
}
