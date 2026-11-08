using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NodaTime;

namespace Nid.Investments.Domain.Prices;

/// <summary>One quote read from a page: the price in its quotation <paramref name="Currency"/>, as of <paramref name="OnDate"/>.</summary>
public sealed record PriceReading(decimal Price, string Currency, LocalDate OnDate);

/// <summary>
/// Boursorama: the search page links the quote page, whose faceplate (<c>data-faceplate-symbol</c>) holds the last price,
/// its currency and its date. Every reader returns null when a marker is missing.
/// </summary>
public static partial class BoursoramaReader
{
    /// <summary>The faceplate is a few kB long; the markers are read inside this window only.</summary>
    private const int FaceplateWindow = 6000;

    public static string? SearchPath(string html)
    {
        try
        {
            var m = QuoteLink().Match(html);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    public static PriceReading? Quote(string html, string path)
    {
        try { return Read(html, path); }
        catch (RegexMatchTimeoutException) { return null; }
    }

    private static PriceReading? Read(string html, string path)
    {
        var symbol = path.TrimEnd('/')[(path.TrimEnd('/').LastIndexOf('/') + 1)..];
        var at = html.IndexOf($"data-faceplate-symbol=\"{symbol}\"", StringComparison.Ordinal);
        if (at < 0) return null;
        var face = html.Substring(at, Math.Min(FaceplateWindow, html.Length - at));

        var last = Last().Match(face);
        var currency = Currency().Match(face);
        if (!last.Success || !currency.Success || !TryNumber(last.Groups[1].Value, out var price)) return null;

        // A fund or tracker states its date in words; a share only in the quote's trade date.
        var date = Written().Match(face);
        var (y, mo, d) = date.Success ? (date.Groups[3], date.Groups[2], date.Groups[1]) : Trade().Match(face) is { Success: true } t ? (t.Groups[1], t.Groups[2], t.Groups[3]) : default;
        if (y is null || !TryDate(y.Value, mo.Value, d.Value, out var on)) return null;
        return new PriceReading(price, currency.Groups[1].Value, on);
    }

    private static bool TryNumber(string text, out decimal value)
    {
        // Thousands are separated by a space, a no-break space or a narrow no-break space.
        var plain = string.Concat(text.Where(c => !char.IsWhiteSpace(c))).Replace(',', '.');
        return decimal.TryParse(plain, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryDate(string y, string m, string d, out LocalDate date)
    {
        date = default;
        if (!int.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out var year) || !int.TryParse(m, NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(d, NumberStyles.None, CultureInfo.InvariantCulture, out var day))
            return false;
        try { date = new LocalDate(year, month, day); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    [GeneratedRegex(@"href=""(/(?:bourse/(?:opcvm|trackers|actions)/)?cours/[^/""]+/)""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex QuoteLink();

    [GeneratedRegex(@"data-ist-last>([^<]+)<", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Last();

    [GeneratedRegex(@"c-faceplate__price-currency"">\s*([A-Z]{3})\s*<", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Currency();

    [GeneratedRegex(@"(?:connu|liquidative) au ([0-9]{2})/([0-9]{2})/([0-9]{4})", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Written();

    [GeneratedRegex(@"tradeDate&quot;:&quot;([0-9]{4})-([0-9]{2})-([0-9]{2})", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Trade();
}

/// <summary>Yahoo Finance: <c>v1/finance/search</c> to find the symbol, <c>v8/finance/chart</c> to quote it.</summary>
public static class YahooReader
{
    private static readonly string[] Kinds = ["ETF", "EQUITY", "MUTUALFUND"];

    public static string? Symbol(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("quotes", out var quotes) || quotes.ValueKind != JsonValueKind.Array) return null;
            foreach (var q in quotes.EnumerateArray())
                if (q.TryGetProperty("quoteType", out var t) && t.ValueKind == JsonValueKind.String && Kinds.Contains(t.GetString())
                    && q.TryGetProperty("symbol", out var s) && s.ValueKind == JsonValueKind.String)
                    return s.GetString();
            return null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; } // a value of another kind than the one read
    }

    public static PriceReading? Quote(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("chart", out var chart)
                || !chart.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0
                || !result[0].TryGetProperty("meta", out var meta)
                || !meta.TryGetProperty("regularMarketPrice", out var p) || !p.TryGetDecimal(out var price)
                || !meta.TryGetProperty("currency", out var c) || c.ValueKind != JsonValueKind.String
                || !meta.TryGetProperty("regularMarketTime", out var time) || !time.TryGetInt64(out var seconds)
                || !meta.TryGetProperty("gmtoffset", out var off) || !off.TryGetInt32(out var offset))
                return null;
            // The date is the exchange's, not UTC's.
            var on = Instant.FromUnixTimeSeconds(seconds).InZone(DateTimeZone.ForOffset(Offset.FromSeconds(offset))).Date;
            return new PriceReading(price, c.GetString()!, on);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentOutOfRangeException) { return null; }
    }
}
