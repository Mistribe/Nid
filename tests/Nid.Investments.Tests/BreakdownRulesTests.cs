using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using NodaTime;

namespace Nid.Investments.Tests;

/// <summary>Holdings exposure spec C4 and D7.</summary>
public class BreakdownRulesTests
{
    private static BreakdownPart P(string code, decimal pct) => new(code, pct);

    private static readonly BreakdownPart[] Valid = [P("north_america", 70m), P("europe_ex_france", 15m), P("japan", 6m), P("emerging", 9m)];

    private static string[] Paths(IReadOnlyList<BreakdownPart> regions, IReadOnlyList<BreakdownPart>? sectors = null)
        => BreakdownRules.Check(regions, sectors).Select(e => $"{e.Path}={e.Code}").ToArray();

    [Fact]
    public void A_breakdown_adding_up_to_100_is_valid_with_or_without_sectors() => Assert.Empty(Paths(Valid));

    [Theory]
    [InlineData(99.9)]
    [InlineData(100.1)]
    public void A_part_not_adding_up_to_100_is_refused_at_its_field(double total)
    {
        var parts = new[] { P("north_america", (decimal)total - 10m), P("japan", 10m) };
        Assert.Equal(["regions=investments.breakdown_not_100"], Paths(parts));
        Assert.Equal(["sectors=investments.breakdown_not_100"], Paths(Valid, parts.Select(p => p with { Code = p.Code == "japan" ? "energy" : "utilities" }).ToArray()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(12.25)]
    public void A_share_is_above_zero_with_one_decimal(double pct)
        => Assert.Contains("regions[1].pct=request.invalid", Paths([P("north_america", 100m), P("japan", (decimal)pct)]));

    [Fact]
    public void An_unknown_or_repeated_code_is_refused_at_its_field()
    {
        Assert.Contains("regions[0].code=request.invalid", Paths([P("usa", 100m)]));
        Assert.Contains("regions[1].code=request.invalid", Paths([P("japan", 50m), P("japan", 50m)]));
    }

    [Theory]
    [InlineData(2025, 11, 1, "etf", true)]
    [InlineData(2025, 11, 3, "etf", false)]
    [InlineData(2020, 1, 1, "equity", false)]
    public void A_breakdown_is_stale_after_365_days_except_for_an_equity(int y, int m, int d, string kind, bool stale)
        => Assert.Equal(stale, BreakdownRules.IsStale(new LocalDate(y, m, d), kind, new LocalDate(2026, 11, 2)));
}
