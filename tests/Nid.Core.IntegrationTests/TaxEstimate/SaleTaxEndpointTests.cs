using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.TestSupport.PropertyApi;
using static Nid.Core.IntegrationTests.TaxEstimate.SaleContributionTests;

namespace Nid.Core.IntegrationTests.TaxEstimate;

/// <summary>Sale tax spec §6: <c>GET /api/v1/tax/sale</c>, the total and an item's page.</summary>
public class SaleTaxEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Route = "/api/v1/tax/sale";

    private static async Task<Guid> HouseAsync(NidApp app, string name, string visibility, PersonId owner)
    {
        var body = Home(app, name, visibility, owner);
        (body["type"], body["purchaseDate"], body["purchasePrice"], body["notaryFees"]) = ("secondary_residence", "2015-06-01", "200000", "12000");
        var id = await NewPropertyAsync(app, body, owner);
        await PostAsync(app, $"{Properties}/{id}/valuations", new { asOf = "2025-06-01", amount = "400000" }, owner);
        return id;
    }

    [Fact]
    public async Task The_total_and_an_items_page()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var house = await HouseAsync(app, "Maison de vacances", "shared", app.AdultA);

        var total = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, Route)).RootElement;
        Assert.True(total.GetProperty("total").GetBoolean());
        Assert.Equal(house, total.GetProperty("items")[0].GetProperty("item").GetGuid());

        var page = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"{Route}?item={house}")).RootElement;
        Assert.False(page.GetProperty("total").GetBoolean());
        Assert.Equal(house, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("item").GetGuid());
    }

    [Fact]
    public async Task The_scope_me_adds_the_readers_private_property_to_the_household_total()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await HouseAsync(app, "Commune", "shared", app.AdultA);
        var mine = await HouseAsync(app, "A moi seul", "private", app.AdultA);

        Guid[] Items(string json) => JsonDocument.Parse(json).RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("item").GetGuid()).ToArray();

        var household = Items(await app.GetRawAsync(app.AdultA, $"{Route}?scope=household"));
        Assert.Equal([shared], household);
        Assert.Equal(new[] { shared, mine }.Order(), Items(await app.GetRawAsync(app.AdultA, $"{Route}?scope=me")).Order());
    }

    [Fact]
    public async Task A_shared_envelopes_page()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var pea = await EnvelopeAsync(app, "PEA commun", "pea", "2015-01-01", [app.AdultB], opening: new { occurredOn = "2015-01-01", amount = Eur("10000") });
        await PostAsync(app, $"{Envelopes}/{pea.Value}/valuations", new { asOf = "2025-10-01", amount = Eur("15000") }, app.AdultB);

        var page = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"{Route}?item={pea.Value}")).RootElement;

        Assert.False(page.GetProperty("total").GetBoolean());
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal((pea.Value, "investments", "15000.00"),
            (item.GetProperty("item").GetGuid(), item.GetProperty("module").GetString(), item.GetProperty("value").GetString()));
    }

    [Fact]
    public async Task Another_adults_private_envelope_is_not_found()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var secret = await EnvelopeAsync(app, "AV secrete", "life_insurance", "2015-01-01", [app.AdultB], "private",
            opening: new { occurredOn = "2015-01-01", amount = Eur("123456") });
        await PostAsync(app, $"{Envelopes}/{secret.Value}/valuations", new { asOf = "2025-10-01", amount = Eur("234567") }, app.AdultB);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Route}?item={secret.Value}");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("AV secrete", body);
        Assert.DoesNotContain("123456", body);
        Assert.DoesNotContain("234567", body);
    }

    [Fact]
    public async Task An_invisible_item_is_not_found()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var secret = await HouseAsync(app, "Pied-a-terre secret", "private", app.AdultB);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Route}?item={secret}");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Pied-a-terre", body);
        Assert.DoesNotContain("400000", body);
        Assert.DoesNotContain("200000", body);
    }
}
