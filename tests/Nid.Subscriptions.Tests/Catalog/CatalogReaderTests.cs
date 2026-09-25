using System.Text;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain.Catalog;

namespace Nid.Subscriptions.Tests.Catalog;

public class CatalogReaderTests
{
    private const string Streaming = """[{"key":"streaming","name":"Streaming","color":"#FF6400F6"}]""";
    private const string Spotify = """{"key":"spotify","name":"Spotify","website":"https://www.spotify.com","categories":["streaming"]}""";

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "catalog", name));

    private static CatalogReading Read(string labels, string providers) => CatalogReader.Read(Utf8(labels), Utf8(providers)).Ok();

    [Fact]
    public void The_real_files_of_the_pinned_commit_read_with_their_two_known_defects()
    {
        var reading = CatalogReader.Read(Fixture("labels.json"), Fixture("providers.json")).Ok();

        Assert.Equal(17, reading.Providers.Count);
        Assert.Empty(reading.Refused);
        Assert.Equal([new CatalogDuplicate(CatalogReader.LabelsFile, "hosting")], reading.Duplicates);
        Assert.Equal(["home"], reading.LabelsCreated);
        Assert.Equal(19, reading.Labels.Count); // 18 distinct keys, plus the one created for home
        Assert.Equal(new CatalogLabel("home", "Home", CatalogLimits.NeutralColor), Assert.Single(reading.Labels, l => l.Key == "home"));
        Assert.Equal(new CatalogLabel("streaming-music", "Music", "#81C784"), Assert.Single(reading.Labels, l => l.Key == "streaming-music"));
        Assert.Equal(new CatalogLabel("work", "Work", "#a855b4"), Assert.Single(reading.Labels, l => l.Key == "work"));

        var netflix = Assert.Single(reading.Providers, p => p.Key == "netflix");
        Assert.Equal(("Netflix", "https://www.netflix.com", "https://www.netflix.com/us/pricing"), (netflix.Name, netflix.Website, netflix.PricingPage));
        Assert.Equal(["streaming", "vod"], netflix.LabelKeys);
        var car = Assert.Single(reading.Providers, p => p.Key == "car-insurance");
        Assert.Equal(((string?)null, (string?)null), (car.Website, car.PricingPage));
        Assert.Equal(["insurance", "home"], Assert.Single(reading.Providers, p => p.Key == "home-insurance").LabelKeys);
    }

    [Fact]
    public void A_target_format_colour_is_kept_as_is_and_an_alpha_one_loses_its_alpha()
    {
        var reading = Read("""[{"key":"streaming","name":"Streaming","color":"#6400F6"},{"key":"vod","name":"VOD","color":"#FFF06292"}]""", "[]");

        Assert.Equal([new CatalogLabel("streaming", "Streaming", "#6400F6"), new CatalogLabel("vod", "VOD", "#F06292")], reading.Labels);
    }

    public static TheoryData<string, string> BrokenProviders => new()
    {
        { """{"key":"Netflix","name":"Netflix","categories":["streaming"]}""", CatalogCodes.InvalidKey },
        { """{"key":"netflix\n","name":"Netflix","categories":["streaming"]}""", CatalogCodes.InvalidKey },
        { $$"""{"key":"{{new string('a', 65)}}","name":"A","categories":["streaming"]}""", CatalogCodes.InvalidKey },
        { """{"name":"Netflix","categories":["streaming"]}""", CatalogCodes.InvalidKey },
        { """{"key":"netflix","name":"   ","categories":["streaming"]}""", CatalogCodes.InvalidName },
        { $$"""{"key":"netflix","name":"{{new string('n', 101)}}","categories":["streaming"]}""", CatalogCodes.InvalidName },
        { """{"key":"netflix","name":"Netflix","website":"ftp://netflix.com","categories":["streaming"]}""", CatalogCodes.InvalidUrl },
        { """{"key":"netflix","name":"Netflix","pricing_page":"javascript:alert(1)","categories":["streaming"]}""", CatalogCodes.InvalidUrl },
        { """{"key":"netflix","name":"Netflix","website":42,"categories":["streaming"]}""", CatalogCodes.InvalidUrl },
        { $$"""{"key":"netflix","name":"Netflix","website":"https://n.com/{{new string('x', 2040)}}","categories":["streaming"]}""", CatalogCodes.InvalidUrl },
        { """{"key":"netflix","name":"Netflix","categories":[]}""", CatalogCodes.NoCategory },
        { """{"key":"netflix","name":"Netflix"}""", CatalogCodes.NoCategory },
        { """{"key":"netflix","name":"Netflix","categories":["Streaming"]}""", CatalogCodes.InvalidCategory },
        { """{"key":"netflix","name":"Netflix","categories":[7]}""", CatalogCodes.InvalidCategory },
        { "42", CatalogCodes.NotAnObject },
    };

    /// <summary>Design §4: an entry breaking a rule is refused and listed with its reason; the rest is read.</summary>
    [Theory]
    [MemberData(nameof(BrokenProviders))]
    public void A_provider_breaking_a_rule_is_refused_and_listed_and_the_rest_is_read(string entry, string reason)
    {
        var reading = Read(Streaming, $"[{entry},{Spotify}]");

        var refused = Assert.Single(reading.Refused);
        Assert.Equal((CatalogReader.ProvidersFile, 0, reason), (refused.File, refused.Index, refused.Reason));
        Assert.Equal("spotify", Assert.Single(reading.Providers).Key);
    }

    public static TheoryData<string, string> BrokenLabels => new()
    {
        { """{"key":"vod","name":"VOD","color":"red"}""", CatalogCodes.InvalidColor },
        { """{"key":"vod","name":"VOD","color":"#FFF"}""", CatalogCodes.InvalidColor },
        { """{"key":"vod","name":"VOD"}""", CatalogCodes.InvalidColor },
        { $$"""{"key":"vod","name":"{{new string('v', 51)}}","color":"#F06292"}""", CatalogCodes.InvalidName },
        { """{"key":"Vod","name":"VOD","color":"#F06292"}""", CatalogCodes.InvalidKey },
        { """{"key":"other","name":"Other","color":"#F06292"}""", CatalogCodes.ReservedKey },
        { "\"vod\"", CatalogCodes.NotAnObject },
    };

    [Theory]
    [MemberData(nameof(BrokenLabels))]
    public void A_label_breaking_a_rule_is_refused_and_listed_and_the_rest_is_read(string entry, string reason)
    {
        var reading = Read($"[{entry},{Streaming.Trim('[', ']')}]", "[]");

        var refused = Assert.Single(reading.Refused);
        Assert.Equal((CatalogReader.LabelsFile, 0, reason), (refused.File, refused.Index, refused.Reason));
        Assert.Equal("streaming", Assert.Single(reading.Labels).Key);
    }

    /// <summary>Review Focus 2: a key is taken exactly as written or refused, never trimmed or lowercased into another one.</summary>
    [Fact]
    public void Keys_are_matched_exactly_never_normalised()
    {
        var reading = Read(Streaming, """
            [{"key":"Netflix","name":"Netflix","categories":["streaming"]},
             {"key":"netflix\n","name":"Netflix","categories":["streaming"]},
             {"key":"netflix-","name":"Netflix dash","categories":["streaming"]},
             {"key":"netflix","name":"Netflix","categories":["streaming"]}]
            """);

        Assert.Equal(["netflix-", "netflix"], reading.Providers.Select(p => p.Key));
        Assert.Equal([(0, CatalogCodes.InvalidKey), (1, CatalogCodes.InvalidKey)], reading.Refused.Select(r => (r.Index, r.Reason)));
        Assert.Empty(reading.Duplicates);
    }

    [Fact]
    public void A_duplicate_key_keeps_its_first_occurrence_and_is_reported()
    {
        var reading = Read(
            """[{"key":"hosting","name":"Hosting","color":"#FFa855b4"},{"key":"hosting","name":"Other hosting","color":"#FF000000"}]""",
            """[{"key":"ovh","name":"OVH","categories":["hosting"]},{"key":"ovh","name":"OVH again","categories":["hosting"]}]""");

        Assert.Equal(new CatalogLabel("hosting", "Hosting", "#a855b4"), Assert.Single(reading.Labels));
        Assert.Equal("OVH", Assert.Single(reading.Providers).Name);
        Assert.Equal([new CatalogDuplicate(CatalogReader.LabelsFile, "hosting"), new CatalogDuplicate(CatalogReader.ProvidersFile, "ovh")], reading.Duplicates);
    }

    [Fact]
    public void A_category_without_a_label_creates_one_named_from_its_key_in_the_neutral_colour()
    {
        var reading = Read(Streaming, """[{"key":"home-insurance","name":"Home insurance","categories":["insurance-home","streaming","streaming"]}]""");

        Assert.Equal(["insurance-home"], reading.LabelsCreated);
        Assert.Equal(new CatalogLabel("insurance-home", "Insurance home", CatalogLimits.NeutralColor), Assert.Single(reading.Labels, l => l.Key == "insurance-home"));
        Assert.Equal(["insurance-home", "streaming"], Assert.Single(reading.Providers).LabelKeys); // a category named twice counts once
    }

    [Fact]
    public void The_local_label_is_a_category_that_needs_no_label()
    {
        var reading = Read(Streaming, """[{"key":"misc","name":"Misc","categories":["other"]}]""");

        Assert.Empty(reading.LabelsCreated);
        Assert.Equal([CatalogLimits.OtherLabel], Assert.Single(reading.Providers).LabelKeys);
    }

    [Fact]
    public void Members_Nid_does_not_read_are_ignored()
    {
        var reading = Read(
            """[{"key":"streaming","name":"Streaming","color":"#FF6400F6","colour":"red"}]""",
            """[{"key":"netflix","name":"Netflix","icon":"https://1000logos.net/n.png","plans":[{"name":{"default":"Standard"}}],"categories":["streaming"]}]""");

        Assert.Empty(reading.Refused);
        Assert.Equal("netflix", Assert.Single(reading.Providers).Key);
    }

    [Theory]
    [InlineData("{\"key\":\"streaming\"}", "[]")]
    [InlineData("[", "[]")]
    [InlineData("[]", "{\"providers\":[]}")]
    [InlineData("[]", "not json")]
    public void A_file_that_is_not_a_JSON_array_fails_the_reading(string labels, string providers)
        => Assert.Equal(CatalogCodes.SourceMalformed, CatalogReader.Read(Utf8(labels), Utf8(providers)).FailureCode());

    [Fact]
    public void A_leading_byte_order_mark_is_tolerated()
    {
        var labels = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8(Streaming)).ToArray();
        Assert.Equal("streaming", Assert.Single(CatalogReader.Read(labels, Utf8("[]")).Ok().Labels).Key);
    }

    private static CatalogReading WithIcon(string iconJson)
        => Read(Streaming, $$"""[{"key":"spotify","name":"Spotify","categories":["streaming"],"icon":{{iconJson}}}]""");

    [Fact]
    public void An_https_icon_is_read()
    {
        var reading = WithIcon("\"https://1000logos.net/spotify.png\"");

        Assert.Equal("https://1000logos.net/spotify.png", Assert.Single(reading.Providers).IconUrl);
        Assert.Empty(reading.IconsRefused);
    }

    [Theory]
    [InlineData("\"http://1000logos.net/spotify.png\"", IconCodes.NotHttps)]
    [InlineData("\"ftp://1000logos.net/spotify.png\"", IconCodes.NotHttps)]
    [InlineData("\"not a url\"", IconCodes.InvalidUrl)]
    [InlineData("\"https://user:secret@1000logos.net/spotify.png\"", IconCodes.InvalidUrl)]
    [InlineData("42", IconCodes.InvalidUrl)]
    [InlineData("{}", IconCodes.InvalidUrl)]
    public void An_icon_breaking_a_rule_is_ignored_and_reported_the_provider_kept(string iconJson, string reason)
    {
        var reading = WithIcon(iconJson);

        Assert.Null(Assert.Single(reading.Providers).IconUrl);
        Assert.Equal([new IconRefusal("spotify", reason)], reading.IconsRefused);
        Assert.Empty(reading.Refused); // the provider itself is not refused
    }

    [Fact]
    public void An_icon_url_longer_than_2048_characters_is_ignored()
    {
        var url = "https://1000logos.net/" + new string('a', CatalogLimits.MaxUrlLength);

        Assert.Equal([new IconRefusal("spotify", IconCodes.InvalidUrl)], WithIcon($"\"{url}\"").IconsRefused);
    }

    [Fact]
    public void An_absent_or_null_icon_is_no_icon_and_nothing_to_report()
    {
        var absent = Read(Streaming, $"[{Spotify}]");
        var none = WithIcon("null");

        Assert.Null(Assert.Single(absent.Providers).IconUrl);
        Assert.Null(Assert.Single(none.Providers).IconUrl);
        Assert.Empty(absent.IconsRefused);
        Assert.Empty(none.IconsRefused);
    }

    [Fact]
    public void An_icon_of_a_refused_provider_is_not_reported_twice()
    {
        var reading = Read(Streaming, """[{"key":"Spotify","name":"Spotify","categories":["streaming"],"icon":"http://x.test/a.png"}]""");

        Assert.Single(reading.Refused);
        Assert.Empty(reading.IconsRefused);
    }

    [Fact]
    public void Every_real_provider_has_its_https_icon()
    {
        var reading = CatalogReader.Read(Fixture("labels.json"), Fixture("providers.json")).Ok();

        Assert.All(reading.Providers, p => Assert.StartsWith("https://", p.IconUrl, StringComparison.Ordinal));
        Assert.Empty(reading.IconsRefused);
        Assert.Equal("https://1000logos.net/wp-content/uploads/2017/05/Netflix-Logo-1536x864.png",
            Assert.Single(reading.Providers, p => p.Key == "netflix").IconUrl);
    }

    [Fact]
    public void The_files_of_the_pinned_commit_read_without_a_defect()
    {
        var reading = CatalogReader.Read(Fixture(Path.Combine("pinned", "labels.json")), Fixture(Path.Combine("pinned", "providers.json"))).Ok();

        Assert.Equal(17, reading.Providers.Count);
        Assert.Equal(19, reading.Labels.Count);
        Assert.Empty(reading.LabelsCreated); // home is a label of its own now
        Assert.Empty(reading.Duplicates); // hosting once
        Assert.Empty(reading.Refused);
        Assert.Empty(reading.IconsRefused);
        Assert.Equal(new CatalogLabel("home", "Home", "#A1887F"), Assert.Single(reading.Labels, l => l.Key == "home"));
        Assert.All(reading.Labels, l => Assert.Matches("^#[0-9A-F]{6}$", l.Color)); // #RRGGBB, upper case, as written
    }
}
