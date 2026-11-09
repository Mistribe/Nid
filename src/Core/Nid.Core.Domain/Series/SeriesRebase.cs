using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Domain.Series;

/// <summary>A series to rebase; <c>Rebased</c> is false for a rate (percent), which has no base. Points ascending by date.</summary>
public sealed record RebaseInput(bool Rebased, IReadOnlyList<SeriesPoint> Points);

/// <summary><c>Base100[i][j]</c> is input i's point j on base 100 at <c>BaseDate</c>; null when the series has no base.</summary>
public sealed record RebaseResult(LocalDate? BaseDate, IReadOnlyList<IReadOnlyList<decimal?>> Base100);

public static class SeriesRebase
{
    /// <summary>
    /// The base date is the first date every rebased series has a value (the latest of their first dates); each series is read as a
    /// step function, so its base is its last value on or before that date. A base at or below zero leaves the series unbased.
    /// </summary>
    public static RebaseResult Rebase(IReadOnlyList<RebaseInput> series)
    {
        var firsts = series.Where(s => s.Rebased && s.Points.Count > 0).Select(s => s.Points[0].Date).ToList();
        LocalDate? baseDate = firsts.Count == 0 ? null : firsts.Max();

        return new(baseDate, series.Select(s => Of(s, baseDate)).ToList());
    }

    private static IReadOnlyList<decimal?> Of(RebaseInput s, LocalDate? baseDate)
    {
        if (!s.Rebased || s.Points.Count == 0 || baseDate is null)
            return s.Points.Select(_ => (decimal?)null).ToList();

        var origin = s.Points.Last(p => p.Date <= baseDate).Value;
        return s.Points
            .Select(p => origin <= 0 ? (decimal?)null : Math.Round(p.Value / origin * 100m, 2, MidpointRounding.AwayFromZero))
            .ToList();
    }
}
