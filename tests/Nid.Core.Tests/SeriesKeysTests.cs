using Nid.Core.Contracts;

namespace Nid.Core.Tests;

public sealed class SeriesKeysTests
{
    [Theory]
    [InlineData("cpi", new[] { "cpi" })]
    [InlineData("cpi,cpi:yoy,fx:EUR", new[] { "cpi", "cpi:yoy", "fx:EUR" })]
    [InlineData("holding:6f9619ff-8b86-d011-b42d-00c04fc964ff:6f9619ff-8b86-d011-b42d-00c04fc964fe:price", new[] { "holding:6f9619ff-8b86-d011-b42d-00c04fc964ff:6f9619ff-8b86-d011-b42d-00c04fc964fe:price" })]
    [InlineData("projection:rate:money_market", new[] { "projection:rate:money_market" })]
    public void Valid_lists_parse_in_order(string s, string[] expected)
    {
        Assert.True(SeriesKeys.TryParse(s, out var keys));
        Assert.Equal(expected, keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cpi,")]
    [InlineData("CPI")]
    [InlineData("cpi!")]
    [InlineData("a:b:c:d:e")]
    [InlineData("cpi,cpi")]
    [InlineData("a,b,c,d,e,f,g")]
    public void Invalid_lists_are_refused(string? s) => Assert.False(SeriesKeys.TryParse(s, out _));

    [Theory]
    [InlineData("Livret A", "livret", true)]
    [InlineData("Épargne", "epar", true)]
    [InlineData(null, "x", false)]
    [InlineData(null, null, true)]
    [InlineData("EUR", " ", true)]
    public void Search_ignores_case_and_accents(string? name, string? q, bool expected)
        => Assert.Equal(expected, SeriesSearch.Matches(name, q));

    [Theory]
    [InlineData("cpi", true)] [InlineData("IRL", false)] [InlineData("inflation", false)] [InlineData("eur", true)] [InlineData("Eu", true)] [InlineData(null, true)]
    public void Search_matches_the_name_or_the_key(string? q, bool expected)
        => Assert.Equal(expected, SeriesSearch.Matches(new SeriesInfo("cpi", SeriesKinds.Cpi, "EUR", SeriesUnits.Index, SeriesFrequencies.Monthly, null, null, null), q));

    [Theory]
    [InlineData("zzz", "cpi", true)] [InlineData("zzz", "irl", false)] [InlineData("cpi", "irl", true)] [InlineData("zzz", "", false)]
    public void Search_also_matches_a_listed_kind(string q, string kind, bool expected)
        => Assert.Equal(expected, SeriesSearch.Matches(new SeriesInfo("cpi", SeriesKinds.Cpi, "EUR", SeriesUnits.Index, SeriesFrequencies.Monthly, null, null, null), q, [kind]));

    [Fact]
    public void Kinds_ignore_the_malformed_and_the_duplicates_and_keep_twenty()
    {
        Assert.Equal(["cpi", "fx_a"], SeriesKeys.ParseKinds("cpi,CPI,1x,cpi,fx_a,,a:b"));
        Assert.Empty(SeriesKeys.ParseKinds(null));
        Assert.Equal(20, SeriesKeys.ParseKinds(string.Join(',', Enumerable.Range(0, 30).Select(i => "k" + new string((char)('a' + i % 26), 1) + new string((char)('a' + i / 26), 1)))).Count);
    }

    [Theory]
    [InlineData(null, "", null)] [InlineData("  ", "", null)] [InlineData(" Maison ", "", "Maison")]
    [InlineData("x", "loan_remaining", null)] [InlineData("REMAIN", "", null)] [InlineData("Prêt", "loan_payment", "Prêt")]
    public void Name_filter_is_null_when_the_kind_itself_matches(string? q, string kinds, string? expected)
        => Assert.Equal(expected, SeriesSearch.NameFilter(q, kinds.Split(',', StringSplitOptions.RemoveEmptyEntries), SeriesKinds.LoanRemaining, "loan", "remaining"));
}
