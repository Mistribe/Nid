using Nid.Core.Application.Projection;
using Nid.Core.Contracts;
using Nid.Core.Domain.Projection;
using Nid.Platform.Contracts;
using NodaTime;

namespace Nid.Core.Application.Series;

/// <summary>The projection's inflation and class rates, year by year over its default grid, as the projection reads them.</summary>
public sealed class ProjectionRateSeries(IHouseholdSettings household, IHouseholdCalendar calendar, IInflation inflation) : ISeriesContribution
{
    private const string InflationKey = "projection:inflation";
    private const string RatePrefix = "projection:rate:";

    public string Module => "core";

    public async Task<IReadOnlyList<SeriesInfo>> ListAsync(PersonId reader, VisibilityScope scope, string? q, CancellationToken ct)
    {
        var context = await ReadAsync(ct);
        var list = new List<SeriesInfo>();
        foreach (var key in new[] { InflationKey }.Concat(AllocationClasses.All.Select(c => RatePrefix + c)))
            if (Build(context, key, null, null) is { Points.Count: > 0 } hit)
                list.Add(hit.Info with { First = hit.Points[0].Date, Last = hit.Points[^1].Date });
        return list.Where(i => SeriesSearch.Matches(i, q)).ToList();
    }

    public async Task<SeriesPoints?> PointsAsync(PersonId reader, VisibilityScope scope, string key, LocalDate? from, LocalDate? to, CancellationToken ct)
    {
        if (!IsKnown(key)) return null;
        return Build(await ReadAsync(ct), key, from, to);
    }

    private static bool IsKnown(string key)
        => key == InflationKey || (key.StartsWith(RatePrefix, StringComparison.Ordinal) && AllocationClasses.All.Contains(key[RatePrefix.Length..]));

    private sealed record Context(LocalDate Today, ProjectionAssumptions Assumptions, decimal? Observed);

    private async Task<Context> ReadAsync(CancellationToken ct)
    {
        var today = await calendar.TodayAsync(ct);
        var a = ProjectionRules.WithoutPastYears(await household.ProjectionAssumptionsAsync(ct), today);
        var observed = await household.ReferenceCurrencyAsync(ct) == Currency.Eur ? (await inflation.ReadAsync(ct)).Last12Months?.Rate : null;
        return new Context(today, a, observed);
    }

    private static SeriesPoints Build(Context c, string key, LocalDate? from, LocalDate? to)
    {
        var @class = key.StartsWith(RatePrefix, StringComparison.Ordinal) ? key[RatePrefix.Length..] : null;
        var (today, a, observed) = (c.Today, c.Assumptions, c.Observed);
        var years = ProjectionRates.GridYears(today, ProjectionHorizon.Default.Years!.Value * 12);

        var points = years
            .Select(y => new SeriesPoint(new LocalDate(y, 1, 1), Math.Round(
                (@class is null ? ProjectionRates.InflationIn(a, observed, today, y) : ProjectionRates.ClassRateIn(a, @class, y)) * 100, 2, MidpointRounding.AwayFromZero)))
            .Where(p => (from is null || p.Date >= from) && (to is null || p.Date <= to)).ToList();
        var info = @class is null
            ? new SeriesInfo(InflationKey, SeriesKinds.ProjectionInflation, null, SeriesUnits.Percent, SeriesFrequencies.Yearly, null, null, null)
            : new SeriesInfo(key, SeriesKinds.ProjectionRate, @class, SeriesUnits.Percent, SeriesFrequencies.Yearly, null, null, null);
        return new SeriesPoints(info, points);
    }
}
