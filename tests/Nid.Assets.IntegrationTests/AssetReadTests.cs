using System.Net;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.Assets.IntegrationTests.AssetApi;

namespace Nid.Assets.IntegrationTests;

public class AssetReadTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<JsonElementList> ListAsync(NidApp app, PersonId actor, string query = "")
    {
        var response = await app.RawAsync(actor, HttpMethod.Get, Route + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonAsync(response);
        return new(json.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("name").GetString()!).ToList(),
            json.GetProperty("total").GetInt32(), json.GetProperty("kinds").EnumerateArray().Select(k => k.GetString()!).ToList());
    }

    private sealed record JsonElementList(IReadOnlyList<string> Names, int Total, IReadOnlyList<string> Kinds);

    [Fact]
    public async Task Private_asset_of_alice_is_invisible_to_bob_in_list_and_404_on_get()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var secret = await CreateAsync(app, Body(app, "Bijou secret", visibility: "private"));
        await CreateAsync(app, Body(app, "Tableau commun"));

        Assert.Equal(["Bijou secret", "Tableau commun"], (await ListAsync(app, app.AdultA, "?scope=me")).Names.Order());
        var bob = await ListAsync(app, app.AdultB, "?scope=me");
        Assert.Equal(["Tableau commun"], bob.Names);
        Assert.Equal(1, bob.Total);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Route}/{secret.Value}")).StatusCode);
    }

    [Fact]
    public async Task List_filters_kind_search_and_pages_on_the_server()
    {
        await using var app = await NidApp.StartAsync(fixture);
        for (var i = 1; i <= 52; i++)
            await CreateAsync(app, Body(app, $"Voiture {i:00}", "vehicle", purchasedOn: "2025-01-10"));
        await CreateAsync(app, Body(app, "Lingot", "art"));

        var all = await ListAsync(app, app.AdultA);
        Assert.Equal(53, all.Total);
        Assert.Equal(50, all.Names.Count);
        Assert.Equal(["art", "vehicle"], all.Kinds);
        Assert.Equal(3, (await ListAsync(app, app.AdultA, "?page=2")).Names.Count);

        var art = await ListAsync(app, app.AdultA, "?kind=art");
        Assert.Equal(["Lingot"], art.Names);
        Assert.Equal(["art", "vehicle"], art.Kinds);
        Assert.Equal(["Voiture 07"], (await ListAsync(app, app.AdultA, "?q=ture 07")).Names);
        Assert.Equal(0, (await ListAsync(app, app.AdultB, "?owner=" + app.AdultB.Value)).Total);
    }
}
