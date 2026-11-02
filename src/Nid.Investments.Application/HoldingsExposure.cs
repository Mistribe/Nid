using System.Globalization;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using NodaTime;

namespace Nid.Investments.Application;

/// <summary>One region or sector: its value and its share of the detailed value, 0.1 %; zero when no holding is in it.</summary>
public sealed record ExposureShareView(string Code, AmountView? Value, decimal? SharePct);

/// <summary>What has no region or sector: outside markets (euro fund, cash), unknown (no breakdown), unclassified sectors.</summary>
public sealed record ExposureRestView(AmountView? Value, decimal? SharePct);

/// <summary>
/// Holdings exposure spec C3: regions and sectors each add back to the detailed value, with <c>OutsideMarkets</c> and <c>Unknown</c>
/// (and <c>UnclassifiedSectors</c> for a breakdown without sectors). <c>StaleValue</c> is the value of the holdings whose breakdown is old (C6).
/// </summary>
public sealed record HoldingsExposureView(
    IReadOnlyList<ExposureShareView> Regions, IReadOnlyList<ExposureShareView> Sectors, ExposureRestView OutsideMarkets, ExposureRestView Unknown,
    ExposureRestView UnclassifiedSectors, AmountView? StaleValue);

internal static class HoldingsExposureMath
{
    /// <summary>Null when a row has no converted value (C3).</summary>
    public static HoldingsExposureView? Build(
        IReadOnlyList<HoldingOverviewRow> rows, IReadOnlyDictionary<string, SecurityBreakdown> latest, string currency, LocalDate today)
    {
        if (rows.Any(r => r.ValueConverted is null)) return null;
        var regions = BreakdownRegions.All.ToDictionary(c => c, _ => 0m);
        var sectors = BreakdownSectors.All.ToDictionary(c => c, _ => 0m);
        decimal total = 0m, outside = 0m, unknown = 0m, unclassified = 0m, stale = 0m;
        foreach (var row in rows)
        {
            var value = decimal.Parse(row.ValueConverted!.Amount, CultureInfo.InvariantCulture);
            total += value;
            if (row.Kind is "euro_fund" or "cash") { outside += value; continue; }
            if (row.Isin is null || !latest.TryGetValue(row.Isin, out var breakdown)) { unknown += value; continue; }
            Split(value, breakdown.Regions, regions);
            if (breakdown.Sectors is null) unclassified += value; else Split(value, breakdown.Sectors, sectors);
            if (BreakdownRules.IsStale(breakdown.AsOf, row.Kind, today)) stale += value;
        }

        AmountView Amount(decimal v) => new(v.ToString("0.00", CultureInfo.InvariantCulture), currency);
        decimal? Share(decimal v) => total > 0m ? EnvelopeMath.RoundPercent(v / total * 100m) : null;
        List<ExposureShareView> Shares(Dictionary<string, decimal> by) => [.. by.Select(p => new ExposureShareView(p.Key, Amount(p.Value), Share(p.Value)))];
        ExposureRestView Rest(decimal v) => new(Amount(v), Share(v));
        return new HoldingsExposureView(Shares(regions), Shares(sectors), Rest(outside), Rest(unknown), Rest(unclassified), Amount(stale));
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
