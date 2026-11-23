using Nid.Investments.Contracts;
using NodaTime;

namespace Nid.Investments.Domain;

/// <summary>A holding the exposure splits: its value converted in the household currency, its kind and its ISIN.</summary>
public sealed record ExposureHolding(decimal Value, string Kind, string? Isin);

/// <summary>A region, a sector or a rest: its value and its share of the detailed value, 0.1 %; null share when the holdings add up to zero.</summary>
public sealed record ExposureShare(string Code, decimal Value, decimal? SharePct);

/// <summary>
/// Holdings exposure spec C3: regions and sectors each add back to the detailed value, with <c>OutsideMarkets</c> and <c>Unknown</c>
/// (and <c>UnclassifiedSectors</c> for a breakdown without sectors). <c>StaleValue</c> is the value of the holdings whose breakdown is old (C6).
/// </summary>
public sealed record HoldingsExposure(
    IReadOnlyList<ExposureShare> Regions, IReadOnlyList<ExposureShare> Sectors, ExposureShare OutsideMarkets, ExposureShare Unknown,
    ExposureShare UnclassifiedSectors, decimal StaleValue)
{
    public const string OutsideMarketsCode = "outside_markets";
    public const string UnknownCode = "unknown";
    public const string UnclassifiedSectorsCode = "unclassified_sectors";

    /// <summary>Each holding split by the latest breakdown of its ISIN; a holding outside markets or without a breakdown goes to a rest.</summary>
    public static HoldingsExposure Of(IReadOnlyList<ExposureHolding> holdings, IReadOnlyDictionary<string, SecurityBreakdown> latest, LocalDate today)
    {
        var regions = BreakdownRegions.All.ToDictionary(c => c, _ => 0m);
        var sectors = BreakdownSectors.All.ToDictionary(c => c, _ => 0m);
        decimal total = 0m, outside = 0m, unknown = 0m, unclassified = 0m, stale = 0m;
        foreach (var holding in holdings)
        {
            var value = holding.Value;
            total += value;
            if (!HoldingWire.IsMarket(holding.Kind)) { outside += value; continue; }
            if (holding.Isin is null || !latest.TryGetValue(holding.Isin, out var breakdown)) { unknown += value; continue; }
            Split(value, breakdown.Regions, regions);
            if (breakdown.Sectors is null) unclassified += value; else Split(value, breakdown.Sectors, sectors);
            if (BreakdownRules.IsStale(breakdown.AsOf, holding.Kind, today)) stale += value;
        }

        ExposureShare Share(string code, decimal v) => new(code, v, total > 0m ? EnvelopeMath.RoundPercent(v / total * 100m) : null);
        return new HoldingsExposure([.. regions.Select(p => Share(p.Key, p.Value))], [.. sectors.Select(p => Share(p.Key, p.Value))],
            Share(OutsideMarketsCode, outside), Share(UnknownCode, unknown), Share(UnclassifiedSectorsCode, unclassified), stale);
    }

    /// <summary>Each part to the cent; the holding's rounding remainder goes to its largest part (the first on a tie).</summary>
    private static void Split(decimal value, IReadOnlyList<BreakdownPart> parts, Dictionary<string, decimal> into)
    {
        var pieces = parts.Select(p => Math.Round(value * p.Pct / 100m, 2, MidpointRounding.AwayFromZero)).ToList();
        var largest = parts.Select((p, i) => (p.Pct, i)).MaxBy(x => x.Pct).i;
        pieces[largest] += value - pieces.Sum();
        for (var i = 0; i < parts.Count; i++) into[parts[i].Code] += pieces[i];
    }
}
