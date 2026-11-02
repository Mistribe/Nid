using Nid.Core.Contracts;
using Nid.Investments.Application;
using Nid.Investments.Contracts;
using NodaTime;

namespace Nid.Investments.Tests;

/// <summary>Holdings exposure spec C3, C6, C9: the region and sector exposure of the holdings overview.</summary>
public class HoldingsExposureTests
{
    private static readonly LocalDate Today = new(2026, 10, 22);
    private static readonly LocalDate Fresh = new(2026, 6, 30);

    private static HoldingOverviewRow Row(string kind, string? isin, string? value, ItemId? envelope = null)
        => new(envelope ?? new ItemId(Guid.Parse("00000000-0000-0000-0000-000000000001")), "Env", Guid.NewGuid(), "H", isin, kind, "EUR", Today,
            value ?? "0", value is null ? null : new AmountView(value, "EUR"), null, null, null, null, null, null);

    private static SecurityBreakdown Breakdown(string isin, LocalDate asOf, (string, decimal)[] regions, (string, decimal)[]? sectors = null)
        => new(Guid.NewGuid(), isin, asOf, [.. regions.Select(r => new BreakdownPart(r.Item1, r.Item2))],
            sectors?.Select(s => new BreakdownPart(s.Item1, s.Item2)).ToList(), null, new PersonId(Guid.NewGuid()), Instant.FromUtc(2026, 1, 1, 0, 0));

    private static IReadOnlyDictionary<string, SecurityBreakdown> By(params SecurityBreakdown[] b) => b.ToDictionary(x => x.Isin);

    private static HoldingsExposureView Build(IReadOnlyList<HoldingOverviewRow> rows, IReadOnlyDictionary<string, SecurityBreakdown> latest)
        => HoldingsExposureMath.Build(rows, latest, "EUR", Today)!;

    private static (string?, decimal?) V(ExposureShareView s) => (s.Value?.Amount, s.SharePct);

    private static ExposureShareView Region(HoldingsExposureView v, string code) => v.Regions.Single(r => r.Code == code);

    private static ExposureShareView Sector(HoldingsExposureView v, string code) => v.Sectors.Single(r => r.Code == code);

    private static decimal Sum(IEnumerable<ExposureShareView> parts, params ExposureRestView[] rest)
        => parts.Sum(p => decimal.Parse(p.Value!.Amount, System.Globalization.CultureInfo.InvariantCulture))
           + rest.Sum(r => decimal.Parse(r.Value!.Amount, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void The_exposure_splits_each_holding_and_adds_up_to_the_detailed_value()
    {
        const string Etf = "IE00B4L5Y983", Bond = "FR0000000001";
        var view = Build(
            [Row("etf", Etf, "10000.00"), Row("euro_fund", null, "5000.00"), Row("fund", "FR0000000002", "2000.00"), Row("fund", Bond, "3000.00")],
            By(Breakdown(Etf, Fresh, [("north_america", 70m), ("europe_ex_france", 15m), ("japan", 6m), ("emerging", 9m)],
                    [("information_technology", 25m), ("financials", 15m), ("health_care", 12m), ("other", 48m)]),
                Breakdown(Bond, Fresh, [("europe_ex_france", 100m)])));

        Assert.Equal(BreakdownRegions.All, view.Regions.Select(r => r.Code));
        Assert.Equal(BreakdownSectors.All, view.Sectors.Select(r => r.Code));
        Assert.Equal(("7000.00", 35.0m), V(Region(view, "north_america")));
        Assert.Equal(("4500.00", 22.5m), V(Region(view, "europe_ex_france")));
        Assert.Equal(("600.00", 3.0m), V(Region(view, "japan")));
        Assert.Equal(("900.00", 4.5m), V(Region(view, "emerging")));
        Assert.Equal(("0.00", 0m), V(Region(view, "france")));
        Assert.Equal(("5000.00", (decimal?)25.0m), (view.OutsideMarkets.Value!.Amount, view.OutsideMarkets.SharePct));
        Assert.Equal(("2000.00", (decimal?)10.0m), (view.Unknown.Value!.Amount, view.Unknown.SharePct));
        Assert.Equal(["2500.00", "1500.00", "1200.00", "4800.00"],
            new[] { "information_technology", "financials", "health_care", "other" }.Select(c => Sector(view, c).Value!.Amount));
        Assert.Equal("3000.00", view.UnclassifiedSectors.Value!.Amount);
        Assert.Equal(20000m, Sum(view.Regions, view.OutsideMarkets, view.Unknown));
        Assert.Equal(20000m, Sum(view.Sectors, view.OutsideMarkets, view.Unknown, view.UnclassifiedSectors));
        Assert.Equal("0.00", view.StaleValue!.Amount);
    }

    [Fact]
    public void The_rounding_remainder_goes_to_the_largest_part()
    {
        const string Isin = "IE00B4L5Y983";
        var view = Build([Row("etf", Isin, "100.01")], By(Breakdown(Isin, Fresh, [("north_america", 33.3m), ("japan", 33.3m), ("emerging", 33.4m)])));

        Assert.Equal(["33.30", "33.30", "33.41"], new[] { "north_america", "japan", "emerging" }.Select(c => Region(view, c).Value!.Amount));
        Assert.Equal(100.01m, Sum(view.Regions, view.OutsideMarkets, view.Unknown));
    }

    [Fact]
    public void A_holding_without_isin_or_without_breakdown_is_unknown()
    {
        var view = Build([Row("etf", null, "100.00"), Row("etf", "IE00B4L5Y983", "50.00")], By());

        Assert.Equal(("150.00", 100m), (view.Unknown.Value!.Amount, view.Unknown.SharePct));
        Assert.All(view.Regions, r => Assert.Equal("0.00", r.Value!.Amount));
    }

    [Fact]
    public void The_same_isin_in_two_envelopes_uses_one_breakdown()
    {
        const string Isin = "IE00B4L5Y983";
        var view = Build(
            [Row("etf", Isin, "100.00", new ItemId(Guid.NewGuid())), Row("etf", Isin, "300.00", new ItemId(Guid.NewGuid()))],
            By(Breakdown(Isin, Fresh, [("north_america", 50m), ("japan", 50m)])));

        Assert.Equal(("200.00", 50m), V(Region(view, "north_america")));
        Assert.Equal(("200.00", 50m), V(Region(view, "japan")));
    }

    [Fact]
    public void A_missing_converted_value_makes_the_exposure_null()
        => Assert.Null(HoldingsExposureMath.Build([Row("etf", null, "100.00"), Row("etf", null, null)], By(), "EUR", Today));

    [Fact]
    public void An_old_breakdown_counts_in_the_stale_value_and_an_equity_never_does()
    {
        const string Etf = "IE00B4L5Y983", Share = "FR0000000003";
        var old = new[] { ("france", 100m) };
        var view = Build([Row("etf", Etf, "400.00"), Row("equity", Share, "2000.00")],
            By(Breakdown(Etf, Today.PlusDays(-400), old), Breakdown(Share, Today.PlusDays(-2000), old)));

        Assert.Equal("400.00", view.StaleValue!.Amount);
        Assert.Equal("2400.00", Region(view, "france").Value!.Amount);
    }

    [Fact]
    public void No_detailed_holding_gives_zero_shares_as_null()
    {
        var view = Build([], By());

        Assert.Equal(("0.00", (decimal?)null), V(Region(view, "france")));
        Assert.Equal("0.00", view.Unknown.Value!.Amount);
        Assert.Null(view.OutsideMarkets.SharePct);
        Assert.Equal("0.00", view.StaleValue!.Amount);
    }
}
