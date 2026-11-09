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

    /// <summary>
    /// The series the reader can see whose name or key contains <c>q</c>, or whose kind is in <c>kinds</c>; every series when <c>q</c> is blank.
    /// Filter in the module's own query (SQL <c>WHERE</c>, read rights included), never by loading the catalogue whole: see <see cref="SeriesSearch.Matches(SeriesInfo, string?, IReadOnlyCollection{string}?)"/> for the rule.
    /// </summary>
    Task<IReadOnlyList<SeriesInfo>> ListAsync(PersonId reader, VisibilityScope scope, string? q, IReadOnlyCollection<string> kinds, CancellationToken ct);

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
    public const string Income = "income";
    public const string IncomeHousehold = "income_household";
    public const string EnvelopeValue = "envelope_value";
    public const string EnvelopeContributions = "envelope_contributions";
    public const string HoldingPrice = "holding_price";
    public const string PropertyValue = "property_value";
    public const string PropertyCost = "property_cost";
    public const string PropertyNet = "property_net";
    public const string TenancyRent = "tenancy_rent";
    public const string LoanRemaining = "loan_remaining";
    public const string LoanPayment = "loan_payment";
}

public static partial class SeriesKeys
{
    public const int MaxKeys = 6;

    [GeneratedRegex("^[a-z][a-z_]*(:[A-Za-z0-9_-]+){0,3}$")]
    private static partial Regex KeyShape();

    [GeneratedRegex("^[a-z][a-z_]*$")]
    private static partial Regex KindShape();

    /// <summary>The comma-separated <c>kinds</c> parameter: malformed codes are ignored, duplicates kept once, at most 20.</summary>
    public static IReadOnlyList<string> ParseKinds(string? kinds)
        => (kinds ?? "").Split(',').Where(k => k.Length <= 40 && KindShape().IsMatch(k)).Distinct().Take(20).ToList();

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

public static class SeriesWindow
{
    /// <summary>The points inside the optional <c>from</c>/<c>to</c> window (inclusive), ascending by date.</summary>
    public static List<SeriesPoint> Within(IEnumerable<SeriesPoint> points, LocalDate? from, LocalDate? to)
        => points.Where(p => (from is null || p.Date >= from) && (to is null || p.Date <= to)).OrderBy(p => p.Date).ToList();
}

/// <summary>An ILIKE pattern for typed text: <c>%</c>, <c>_</c> and the backslash match themselves (use <c>\</c> as the escape character).</summary>
public static class LikePattern
{
    public static string Contains(string text) => $"%{text.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")}%";
}

public static class SeriesSearch
{
    /// <summary>The query matches the series' name or its key, or the series' kind is one of <paramref name="kinds"/>.</summary>
    public static bool Matches(SeriesInfo info, string? q, IReadOnlyCollection<string>? kinds = null)
        => Matches(info.Name, q) || Matches(info.Key, q) || kinds?.Contains(info.Kind) == true;

    /// <summary>Case- and accent-insensitive substring; a blank query matches everything.</summary>
    public static bool Matches(string? name, string? q)
        => string.IsNullOrWhiteSpace(q)
           || (name is not null && CultureInfo.InvariantCulture.CompareInfo.IndexOf(
               name, q.Trim(), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);

    /// <summary>
    /// The name filter to push into an item query: null when every item of <paramref name="kind"/> matches (blank query, the kind
    /// asked for, or a query that matches one of the kind's <paramref name="keyWords"/>), else the trimmed query.
    /// </summary>
    public static string? NameFilter(string? q, IReadOnlyCollection<string> kinds, string kind, params string[] keyWords)
        => string.IsNullOrWhiteSpace(q) || kinds.Contains(kind) || keyWords.Any(w => Matches(w, q)) ? null : q.Trim();
}
