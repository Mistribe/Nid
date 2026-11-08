using System.Globalization;
using System.Xml.Linq;
using NodaTime;
using NodaTime.Text;

namespace Nid.RealEstate.Domain;

/// <summary>Rental spec §3: an IRL quarter, written <c>2026-Q2</c>.</summary>
public sealed record IrlQuarter(int Year, int Quarter)
{
    public static IrlQuarter Parse(string text)
    {
        if (text.Length == 7 && text[4] == '-' && text[5] == 'Q'
            && int.TryParse(text.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            && text[6] is >= '1' and <= '4')
            return new IrlQuarter(year, text[6] - '0');
        throw new FormatException($"Not an IRL quarter: '{text}'.");
    }

    public override string ToString() => $"{Year}-Q{Quarter}";
}

/// <summary>Rental spec §3: a stored IRL value. <c>Source</c> is <c>insee</c> or <c>manual</c>.</summary>
public sealed record IrlValue(IrlQuarter Quarter, decimal Value, LocalDate? PublishedOn, LocalDate FetchedOn, string Source);

/// <summary>Rental spec §3: reads the INSEE BDM series 001515333 (SDMX XML), one <c>Obs</c> element per quarter.</summary>
public static class IrlReader
{
    /// <summary>Every observation of the document. Throws <see cref="FormatException"/> on a malformed one.</summary>
    public static IReadOnlyList<(IrlQuarter Quarter, decimal Value, LocalDate? PublishedOn)> Read(ReadOnlySpan<byte> sdmx)
    {
        try
        {
            var doc = XDocument.Load(new MemoryStream(sdmx.ToArray()));
            var observations = doc.Descendants().Where(e => e.Name.LocalName == "Obs").Select(o =>
            {
                var quarter = IrlQuarter.Parse((string?)o.Attribute("TIME_PERIOD") ?? throw new FormatException("Obs without TIME_PERIOD."));
                var value = decimal.Parse((string?)o.Attribute("OBS_VALUE") ?? throw new FormatException("Obs without OBS_VALUE."),
                    NumberStyles.Number, CultureInfo.InvariantCulture);
                var jo = (string?)o.Attribute("DATE_JO");
                LocalDate? published = jo is null ? null : LocalDatePattern.Iso.Parse(jo).GetValueOrThrow();
                return (quarter, value, published);
            }).ToList();
            // INSEE never answers an empty IRL series: that is a changed format or a wrong series id.
            return observations.Count > 0 ? observations : throw new FormatException("The document holds no IRL observation.");
        }
        catch (Exception e) when (e is System.Xml.XmlException or UnparsableValueException or FormatException or OverflowException)
        {
            throw e as FormatException ?? new FormatException("Malformed IRL document.", e);
        }
    }
}

/// <summary>Rental spec §3: a quarter whose change against the same quarter a year earlier leaves [−5 %, +10 %] is rejected.</summary>
public static class IrlPlausibility
{
    public static bool Plausible(decimal value, decimal? sameQuarterLastYear)
        => sameQuarterLastYear is not { } last || (value >= last * 0.95m && value <= last * 1.10m);
}

/// <summary>Rental spec §3: the stored IRL series, <c>realestate.irl</c>, one value per quarter.</summary>
public interface IIrlStore
{
    /// <summary>Every stored value, by quarter.</summary>
    Task<IReadOnlyList<IrlValue>> ListAsync(CancellationToken ct);

    /// <summary>Stores the quarter's value, replacing the one stored (the collector decides what may replace what).</summary>
    Task UpsertAsync(IrlValue value, CancellationToken ct);

    /// <summary>Stores a manual value in one statement unless the quarter holds an <c>insee</c> one; false when it did not write.</summary>
    Task<bool> UpsertManualAsync(IrlValue value, CancellationToken ct);
}
