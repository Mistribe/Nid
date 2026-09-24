using System.IO.Compression;
using System.Text;
using Nid.Core.Contracts;
using Nid.Core.Domain.Fx;
using NodaTime;

namespace Nid.Core.Tests;

public class FxReaderTests
{
    private static readonly LocalDate Day = new(2026, 9, 20);

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        return buffer.ToArray();
    }

    private static string? FailureCode<T>(UnambitiousFx.Functional.Result<T> result) where T : notnull
        => result.TryGetFailure(out var failure) ? failure!.Code : null;

    [Fact]
    public void SubTracker_reads_a_day_in_USD()
    {
        var reading = SubTrackerReader.Read(Utf8("""{ "from": "USD", "to": { "EUR": 0.870604, "GBP": 0.746547 } }"""), Day).Ok();

        Assert.Equal(
            [new FxRate(Day, Currency.Eur, 0.870604m, FxRateSource.SubTrackerData), new FxRate(Day, Code("GBP"), 0.746547m, FxRateSource.SubTrackerData)],
            reading.Rates.OrderBy(r => r.Quote.Code).ToList());
        Assert.Empty(reading.Rejected);
    }

    [Fact]
    public void SubTracker_skips_USD_itself()
    {
        var reading = SubTrackerReader.Read(Utf8("""{ "from": "USD", "to": { "USD": 1, "EUR": 0.87 } }"""), Day).Ok();
        Assert.DoesNotContain(reading.Rates, r => r.Quote == Currency.Usd);
        Assert.Single(reading.Rates);
    }

    [Fact]
    public void SubTracker_in_another_base_is_rebased_on_USD()
    {
        var reading = SubTrackerReader.Read(Utf8("""{ "from": "EUR", "to": { "USD": 1.25, "GBP": 0.75 } }"""), Day).Ok();

        Assert.Equal(0.8m, Assert.Single(reading.Rates, r => r.Quote == Currency.Eur).Rate);
        Assert.Equal(0.6m, Assert.Single(reading.Rates, r => r.Quote.Code == "GBP").Rate);
    }

    [Fact]
    public void SubTracker_rejects_a_bad_value_or_code_and_keeps_the_rest()
    {
        var reading = SubTrackerReader.Read(Utf8("""{ "from": "USD", "to": { "EUR": -1, "gbp": 0.7, "CHF": "x", "JPY": 147.2 } }"""), Day).Ok();

        Assert.Equal("JPY", Assert.Single(reading.Rates).Quote.Code);
        Assert.Equal(
            [("CHF", FxCodes.InvalidValue), ("EUR", FxCodes.InvalidValue), ("gbp", FxCodes.InvalidCode)],
            reading.Rejected.Select(r => (r.Quote, r.Reason)).OrderBy(r => r.Quote, StringComparer.Ordinal).ToList());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "to": { "EUR": 0.8 } }""")]
    [InlineData("""{ "from": "EUR", "to": { "GBP": 0.8 } }""")] // another base without USD cannot be rebased
    [InlineData("""[1, 2]""")]
    public void SubTracker_refuses_a_malformed_file(string json)
        => Assert.Equal(FxCodes.SourceMalformed, FailureCode(SubTrackerReader.Read(Utf8(json), Day)));

    [Fact]
    public void A_rate_too_large_for_the_column_is_rejected()
    {
        var reading = SubTrackerReader.Read(Utf8("""{ "from": "USD", "to": { "VES": 1000000000, "EUR": 0.8 } }"""), Day).Ok();
        Assert.Equal(("VES", FxCodes.OutOfRange), (Assert.Single(reading.Rejected).Quote, reading.Rejected[0].Reason));
    }

    private const string EcbCsv = """
        Date,USD,JPY,CYP,GBP,
        2026-09-22,1.25,180.17,N/A,0.75,
        2026-09-21,N/A,180.7,N/A,0.8578,
        1999-01-04,1.1789,133.73,0.58231,0.7111,

        """;

    [Fact]
    public void The_ECB_history_is_rebased_on_USD_withdrawn_currencies_included()
    {
        var reading = EcbReader.Read(Zip(("eurofxref-hist.csv", EcbCsv))).Ok();

        var latest = reading.Rates.Where(r => r.OnDate == new LocalDate(2026, 9, 22)).ToDictionary(r => r.Quote.Code, r => r.Rate);
        Assert.Equal(0.8m, latest["EUR"]);
        Assert.Equal(0.6m, latest["GBP"]);
        Assert.Equal(144.136m, latest["JPY"]);
        Assert.False(latest.ContainsKey("CYP")); // N/A that day
        Assert.All(reading.Rates, r => Assert.Equal(FxRateSource.Ecb, r.Source));

        Assert.Contains(reading.Rates, r => r.OnDate == new LocalDate(1999, 1, 4) && r.Quote.Code == "CYP");
    }

    [Fact]
    public void An_ECB_row_without_USD_is_skipped()
    {
        var reading = EcbReader.Read(Zip(("eurofxref-hist.csv", EcbCsv))).Ok();
        Assert.DoesNotContain(reading.Rates, r => r.OnDate == new LocalDate(2026, 9, 21));
    }

    [Fact]
    public void A_duplicated_ECB_date_row_yields_one_rate_per_quote_the_first_row_winning()
    {
        const string csv = """
            Date,USD,GBP,
            2026-09-22,1.25,0.75,
            2026-09-22,1.6,0.8,
            2026-09-21,1.25,0.8,

            """;

        var reading = EcbReader.Read(Zip(("eurofxref-hist.csv", csv))).Ok();

        var day = reading.Rates.Where(r => r.OnDate == new LocalDate(2026, 9, 22)).ToList();
        Assert.Equal(2, day.Count); // EUR and GBP, once each
        Assert.Equal(0.8m, Assert.Single(day, r => r.Quote == Currency.Eur).Rate);  // 1 / 1.25, the first row
        Assert.Equal(0.6m, Assert.Single(day, r => r.Quote == Code("GBP")).Rate);   // 0.75 / 1.25
    }

    [Fact]
    public void An_ECB_zip_with_two_entries_or_not_a_zip_is_malformed()
    {
        Assert.Equal(FxCodes.SourceMalformed, FailureCode(EcbReader.Read(Zip(("a.csv", EcbCsv), ("b.csv", EcbCsv)))));
        Assert.Equal(FxCodes.SourceMalformed, FailureCode(EcbReader.Read(Utf8("not a zip"))));
    }

    [Fact]
    public void An_ECB_entry_over_the_decompressed_bound_is_malformed()
        => Assert.Equal(FxCodes.SourceMalformed, FailureCode(EcbReader.Read(Zip(("eurofxref-hist.csv", EcbCsv)), maxUncompressedBytes: 64)));

    private static Currency Code(string code) => Currency.TryCreate(code, out var c) ? c : throw new ArgumentException(code);
}
