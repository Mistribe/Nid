using Nid.Investments.Domain.Prices;
using NodaTime;

namespace Nid.Investments.Tests.Prices;

public class PriceReaderTests
{
    private const string FundPath = "/bourse/opcvm/cours/MP-800128/";

    private static string Vector(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "prices", name));

    [Fact] public void Finds_the_fund_path() => Assert.Equal(FundPath, BoursoramaReader.SearchPath(Vector("boursorama-search-fund.html")));
    [Fact] public void Finds_the_tracker_path() => Assert.Equal("/bourse/trackers/cours/1rTCW8/", BoursoramaReader.SearchPath(Vector("boursorama-search-etf.html")));
    [Fact] public void Finds_the_share_path() => Assert.Equal("/cours/1rPTTE/", BoursoramaReader.SearchPath(Vector("boursorama-search-equity.html")));

    [Fact] public void Reads_a_fund_nav() => Assert.Equal(new PriceReading(3088.54m, "EUR", new LocalDate(2026, 10, 21)), BoursoramaReader.Quote(Vector("boursorama-fund.html"), FundPath));

    [Fact]
    public void Reads_a_tracker_and_a_share()
    {
        Assert.Equal(new PriceReading(705.79m, "EUR", new LocalDate(2026, 10, 23)), BoursoramaReader.Quote(Vector("boursorama-etf.html"), "/bourse/trackers/cours/1rTCW8/"));
        Assert.Equal(new PriceReading(81.38m, "EUR", new LocalDate(2026, 10, 23)), BoursoramaReader.Quote(Vector("boursorama-equity.html"), "/cours/1rPTTE/"));
    }

    [Theory]
    [InlineData(' ')]
    [InlineData(' ')]
    public void Reads_a_price_with_a_no_break_space(char space)
    {
        var html = Vector("boursorama-fund.html").Replace("3 088,54", $"3{space}088,54");
        Assert.Equal(3088.54m, BoursoramaReader.Quote(html, FundPath)?.Price);
    }

    [Fact] public void A_page_without_the_symbol_is_null() => Assert.Null(BoursoramaReader.Quote(Vector("boursorama-fund.html"), "/bourse/opcvm/cours/MP-000000/"));
    [Fact] public void A_search_without_a_quote_link_is_null() => Assert.Null(BoursoramaReader.SearchPath("<html></html>"));

    [Fact]
    public void Reads_the_yahoo_symbol_and_quote()
    {
        Assert.Equal("CW8.PA", YahooReader.Symbol(Vector("yahoo-search.json")));
        Assert.Equal(new PriceReading(706.0m, "EUR", new LocalDate(2026, 10, 23)), YahooReader.Quote(Vector("yahoo-chart.json")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"chart\":{\"result\":[]}}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"quotes\":[1]}")]
    [InlineData("{\"chart\":[]}")]
    public void Malformed_yahoo_json_is_null(string json)
    {
        Assert.Null(YahooReader.Symbol(json));
        Assert.Null(YahooReader.Quote(json));
    }

    [Fact]
    public void A_chart_without_a_numeric_price_is_null()
        => Assert.Null(YahooReader.Quote(Vector("yahoo-chart.json").Replace("\"regularMarketPrice\":706.0", "\"regularMarketPrice\":null")));

    [Theory]
    [InlineData("c-faceplate__price-currency\">", "c-faceplate__price-currency-x\">")]
    [InlineData("connu au ", "connu le ")]
    [InlineData("3 088,54", "n/a")]
    public void An_unreadable_boursorama_page_is_null(string from, string to)
    {
        var html = Vector("boursorama-fund.html");
        Assert.Contains(from, html);
        Assert.Null(BoursoramaReader.Quote(html.Replace(from, to), FundPath));
    }
}
