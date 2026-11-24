using System.Globalization;
using System.Xml.Linq;
using NodaTime;
using NodaTime.Text;

namespace Nid.Core.Domain.Inflation;

/// <summary>A stored CPI value (INSEE series 011814630). <c>Source</c> is <c>insee</c> or <c>manual</c>.</summary>
public sealed record CpiValue(YearMonth Month, decimal Value, LocalDate? PublishedOn, LocalDate FetchedOn, string Source);

/// <summary>Reads the INSEE BDM series 011814630 (SDMX XML), one <c>Obs</c> element per month.</summary>
public static class CpiReader
{
    /// <summary>
    /// The definitive observations of the document (<c>OBS_QUAL="DEF"</c>; the provisional month has no <c>DATE_JO</c>).
    /// Throws <see cref="FormatException"/> on a malformed document or one with no definitive observation.
    /// </summary>
    public static IReadOnlyList<(YearMonth Month, decimal Value, LocalDate? PublishedOn)> Read(ReadOnlySpan<byte> sdmx)
    {
        try
        {
            var doc = XDocument.Load(new MemoryStream(sdmx.ToArray()));
            var observations = doc.Descendants().Where(e => e.Name.LocalName == "Obs")
                .Where(o => (string?)o.Attribute("OBS_QUAL") == "DEF").Select(o =>
                {
                    var month = YearMonthPattern.Iso.Parse((string?)o.Attribute("TIME_PERIOD") ?? throw new FormatException("Obs without TIME_PERIOD.")).GetValueOrThrow();
                    var value = decimal.Parse((string?)o.Attribute("OBS_VALUE") ?? throw new FormatException("Obs without OBS_VALUE."),
                        NumberStyles.Number, CultureInfo.InvariantCulture);
                    var jo = (string?)o.Attribute("DATE_JO");
                    LocalDate? published = jo is null ? null : LocalDatePattern.Iso.Parse(jo).GetValueOrThrow();
                    return (month, value, published);
                }).ToList();
            // INSEE never answers an empty CPI series: that is a changed format or a wrong series id.
            return observations.Count > 0 ? observations : throw new FormatException("The document holds no definitive CPI observation.");
        }
        catch (FormatException e) when (e is not UnparsableValueException)
        {
            throw;
        }
        // NodaTime's UnparsableValueException is itself a FormatException: wrap it to report a CPI document.
        catch (Exception e) when (e is System.Xml.XmlException or UnparsableValueException or OverflowException)
        {
            throw new FormatException("Malformed CPI document.", e);
        }
    }
}

/// <summary>A month more than 3 % away from the previous month, either way, is rejected.</summary>
public static class CpiPlausibility
{
    public static bool Plausible(decimal value, decimal? previousMonth)
        => previousMonth is not { } last || (value >= last * 0.97m && value <= last * 1.03m);
}
