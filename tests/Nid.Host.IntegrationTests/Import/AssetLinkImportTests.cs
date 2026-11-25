using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using Nid.Host.IntegrationTests.Export;

namespace Nid.Host.IntegrationTests.Import;

/// <summary>#648: the asset a subscription relates to travels in the file, as <c>asset</c>, resolved from the section <c>assets</c>.</summary>
public class AssetLinkImportTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Car = """
        {"id":"asset-car","name":"Voiture","kind":"vehicle","currency":"EUR","purchase_price":"15000.00","purchased_on":"2023-05-01","ownership":[{"person":"adult-1","share_bp":10000}]}
        """;

    private static string Subscription(string asset = "asset-car")
        => $$"""[{"id":"sub-car","name":"Assurance auto","asset":"{{asset}}","ownership":[{"person":"adult-1","share_bp":10000}],"auto_renew":true}]""";

    private static Dictionary<string, string> File(string subscriptions) => new()
    {
        ["persons"] = GoldenFile.Persons, ["assets"] = "[" + Car + "]", ["subscriptions"] = subscriptions,
    };

    [Fact]
    public async Task Round_trip_keeps_the_asset_link()
    {
        await using var source = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(source, "Anna");
        await RoundTripTests.CommitAsync(source, source.AdultA, File(Subscription()));

        var first = await new ExportClient(source).ExportAllAsync(source.AdultA);
        Assert.Equal("asset-car", Assert.Single(first["subscriptions"]).GetProperty("asset").GetString());

        await using var target = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(target, "Zoé");
        await RoundTripTests.CommitAsync(target, target.AdultA, RoundTripTests.AsFile(first));

        var list = await target.GetAsync<JsonElement>(target.AdultA, "/api/v1/assets");
        var asset = list.GetProperty("rows")[0].GetProperty("id").GetGuid();
        var detail = await target.GetAsync<JsonElement>(target.AdultA, $"/api/v1/assets/{asset}");
        Assert.Equal("Assurance auto", Assert.Single(detail.GetProperty("subscriptions").EnumerateArray()).GetProperty("name").GetString());
        var second = await new ExportClient(target).ExportAllAsync(target.AdultA);
        Assert.Equal(JsonSerializer.Serialize(first["subscriptions"]), JsonSerializer.Serialize(second["subscriptions"]));
    }

    [Fact]
    public async Task An_unknown_asset_is_an_unresolved_reference()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA, new Dictionary<string, Guid> { ["adult-1"] = app.AdultA.Value, ["adult-2"] = app.AdultB.Value });
        foreach (var (section, entities) in File(Subscription("asset-nope")))
            Assert.True((await client.PutAsync(app.AdultA, session.Id, section, 0, entities)).Accepted);

        var validated = await client.ValidateAsync(app.AdultA, session.Id, false);

        Assert.Equal([(ImportCodes.UnresolvedReference, "subscriptions[0].asset")],
            validated.Report!.Issues.Where(i => i.Severity == "error").Select(i => (i.Code, i.Path)));
    }
}
