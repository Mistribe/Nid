using Nid.Core.Contracts;
using Nid.Core.Domain.Inflation;
using Nid.Platform.Contracts;
using NodaTime;

namespace Nid.Core.Application.Series;

/// <summary>The stored CPI as two series: the index (<c>cpi</c>) and its change over twelve months (<c>cpi:yoy</c>).</summary>
public sealed class CpiSeries(ICpiStore store) : ISeriesContribution
{
    private const string Index = "cpi";
    private const string Yoy = "cpi:yoy";

    public string Module => "core";

    public async Task<IReadOnlyList<SeriesInfo>> ListAsync(PersonId reader, VisibilityScope scope, string? q, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(q)) return [];
        var list = new List<SeriesInfo>();
        foreach (var key in new[] { Index, Yoy })
            if (await PointsAsync(reader, scope, key, null, null, ct) is { Points.Count: > 0 } hit)
                list.Add(hit.Info with { First = hit.Points[0].Date, Last = hit.Points[^1].Date });
        return list;
    }

    public async Task<SeriesPoints?> PointsAsync(PersonId reader, VisibilityScope scope, string key, LocalDate? from, LocalDate? to, CancellationToken ct)
    {
        if (key is not (Index or Yoy)) return null;
        var stored = (await store.ListAsync(ct)).ToDictionary(v => v.Month, v => v.Value);
        var points = key == Index
            ? stored.Select(m => new SeriesPoint(Day(m.Key), m.Value))
            : stored.Where(m => stored.ContainsKey(m.Key.PlusMonths(-12)))
                .Select(m => new SeriesPoint(Day(m.Key), Math.Round((m.Value / stored[m.Key.PlusMonths(-12)] - 1) * 100, 1, MidpointRounding.AwayFromZero)));
        var info = key == Index
            ? new SeriesInfo(Index, SeriesKinds.Cpi, null, SeriesUnits.Index, SeriesFrequencies.Monthly, null, null, null)
            : new SeriesInfo(Yoy, SeriesKinds.CpiYoy, null, SeriesUnits.Percent, SeriesFrequencies.Monthly, null, null, null);
        return new SeriesPoints(info, points.Where(p => (from is null || p.Date >= from) && (to is null || p.Date <= to)).OrderBy(p => p.Date).ToList());
    }

    private static LocalDate Day(YearMonth m) => new(m.Year, m.Month, 1);
}
