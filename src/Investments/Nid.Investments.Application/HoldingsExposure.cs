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

/// <summary>The exposure in the household currency, from the holdings overview's converted values.</summary>
internal static class HoldingsExposureViews
{
    /// <summary>Null when a row has no converted value (C3).</summary>
    public static HoldingsExposureView? Build(
        IReadOnlyList<HoldingOverviewRow> rows, IReadOnlyDictionary<string, SecurityBreakdown> latest, string currency, LocalDate today)
    {
        if (rows.Any(r => r.ValueConverted is null)) return null;
        var exposure = HoldingsExposure.Of(
            [.. rows.Select(r => new ExposureHolding(decimal.Parse(r.ValueConverted!.Amount, CultureInfo.InvariantCulture), r.Kind, r.Isin))], latest, today);

        AmountView Amount(decimal v) => new(v.ToString("0.00", CultureInfo.InvariantCulture), currency);
        List<ExposureShareView> Shares(IReadOnlyList<ExposureShare> by) => [.. by.Select(p => new ExposureShareView(p.Code, Amount(p.Value), p.SharePct))];
        ExposureRestView Rest(ExposureShare v) => new(Amount(v.Value), v.SharePct);
        return new HoldingsExposureView(Shares(exposure.Regions), Shares(exposure.Sectors), Rest(exposure.OutsideMarkets), Rest(exposure.Unknown),
            Rest(exposure.UnclassifiedSectors), Amount(exposure.StaleValue));
    }
}
