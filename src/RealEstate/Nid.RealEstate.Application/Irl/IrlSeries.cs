using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Application.Irl;

/// <summary>The stored rent reference index as the <c>irl</c> series, each quarter dated its first day.</summary>
public sealed class IrlSeries(IIrlStore store) : ISeriesContribution
{
    private const string Key = "irl";

    public string Module => "realestate";

    public async Task<IReadOnlyList<SeriesInfo>> ListAsync(PersonId reader, VisibilityScope scope, string? q, IReadOnlyCollection<string> kinds, CancellationToken ct)
    {
        if (await PointsAsync(reader, scope, Key, null, null, ct) is not { Points.Count: > 0 } hit) return [];
        var info = hit.Info with { First = hit.Points[0].Date, Last = hit.Points[^1].Date };
        return SeriesSearch.Matches(info, q, kinds) ? [info] : [];
    }

    public async Task<SeriesPoints?> PointsAsync(PersonId reader, VisibilityScope scope, string key, LocalDate? from, LocalDate? to, CancellationToken ct)
    {
        if (key != Key) return null;
        var points = SeriesWindow.Within((await store.ListAsync(ct))
            .Select(v => new SeriesPoint(new LocalDate(v.Quarter.Year, (v.Quarter.Quarter - 1) * 3 + 1, 1), v.Value)), from, to);
        return new SeriesPoints(new SeriesInfo(Key, SeriesKinds.Irl, null, SeriesUnits.Index, SeriesFrequencies.Quarterly, null, null, null), points);
    }
}
