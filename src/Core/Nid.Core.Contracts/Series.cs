using System.Globalization;
using System.Text.RegularExpressions;
using NodaTime;

namespace Nid.Core.Contracts;

/// <summary>One series of the explorer's catalogue; <c>Item</c> is set for a series that belongs to an item, so the output guard can check it.</summary>
public sealed record SeriesInfo(string Key, string Kind, string? Name, string Unit, string Frequency, LocalDate? First, LocalDate? Last, ItemId? Item);

public sealed record SeriesPoint(LocalDate Date, decimal Value);

/// <summary>What <see cref="ISeriesContribution.PointsAsync"/> answers: the series' description and its points, ascending by date.</summary>
public sealed record SeriesPoints(SeriesInfo Info, IReadOnlyList<SeriesPoint> Points);

/// <summary>A module's share of the series catalogue.</summary>
public interface ISeriesContribution
{
    string Module { get; }

    Task<IReadOnlyList<SeriesInfo>> ListAsync(PersonId reader, VisibilityScope scope, string? q, CancellationToken ct);

    /// <summary>Null when the key is not this contribution's, unknown, or not visible; answers at once (no I/O) for a key outside its kinds.</summary>
    Task<SeriesPoints?> PointsAsync(PersonId reader, VisibilityScope scope, string key, LocalDate? from, LocalDate? to, CancellationToken ct);
}

public static class SeriesCodes
{
    public const string InvalidKey = "core.series_invalid_key";
    public const string NotFound = "core.series_not_found";
}

public static class SeriesUnits
{
    public const string Index = "index";
    public const string Percent = "percent";

    public static string Currency(Currency c) => $"currency:{c.Code}";

    public static string Rate(Currency c) => $"rate:{c.Code}";
}

public static class SeriesFrequencies
{
    public const string Daily = "daily";
    public const string Monthly = "monthly";
    public const string Quarterly = "quarterly";
    public const string Yearly = "yearly";
    public const string Irregular = "irregular";
}

public static class SeriesKinds
{
    public const string Cpi = "cpi";
    public const string CpiYoy = "cpi_yoy";
    public const string Irl = "irl";
    public const string Fx = "fx";
    public const string ProjectionInflation = "projection_inflation";
    public const string ProjectionRate = "projection_rate";
}

public static partial class SeriesKeys
{
    public const int MaxKeys = 6;

    [GeneratedRegex("^[a-z][a-z_]*(:[A-Za-z0-9_-]+){0,3}$")]
    private static partial Regex KeyShape();

    /// <summary>The comma-separated <c>s</c> parameter: 1 to <see cref="MaxKeys"/> distinct well-formed keys, no trimming.</summary>
    public static bool TryParse(string? s, out IReadOnlyList<string> keys)
    {
        var parts = s?.Split(',') ?? [];
        keys = parts;
        if (s is null || parts.Length > MaxKeys || parts.Distinct().Count() != parts.Length
            || parts.Any(k => k.Length > 200 || !KeyShape().IsMatch(k)))
        {
            keys = [];
            return false;
        }

        return true;
    }
}

public static class SeriesSearch
{
    /// <summary>The query matches the series' name or its key.</summary>
    public static bool Matches(SeriesInfo info, string? q) => Matches(info.Name, q) || Matches(info.Key, q);

    /// <summary>Case- and accent-insensitive substring; a blank query matches everything.</summary>
    public static bool Matches(string? name, string? q)
        => string.IsNullOrWhiteSpace(q)
           || (name is not null && CultureInfo.InvariantCulture.CompareInfo.IndexOf(
               name, q.Trim(), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);
}
