using System.Net;
using Nid.Assets.Contracts;
using Nid.TestSupport;
using static Nid.Assets.IntegrationTests.AssetApi;

namespace Nid.Assets.IntegrationTests;

/// <summary>An asset's valuations. Today is <see cref="NidApp.GoldenCaseToday"/> (2025-10-15).</summary>
public class AssetValuationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static Task<HttpResponseMessage> RecordAsync(NidApp app, Nid.Core.Contracts.ItemId id, string asOf, string amount)
        => app.RawAsync(app.AdultA, HttpMethod.Post, $"{Route}/{id.Value}/valuations", new { asOf, amount, note = "Expertise" });

    [Fact]
    public async Task Record_then_void()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app, Body(app));

        var recorded = await RecordAsync(app, id, "2025-06-01", "1500");
        Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);
        var valuation = (await JsonAsync(recorded)).GetProperty("id").GetGuid();
        var view = await GetAsync(app, id);
        Assert.False(view.GetProperty("valueFromPurchase").GetBoolean());
        Assert.Equal("1500.00", view.GetProperty("value").GetProperty("amount").GetString());

        var voided = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Route}/{id.Value}/valuations/{valuation}");
        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        Assert.Empty((await GetAsync(app, id)).GetProperty("valuations").EnumerateArray());
    }

    [Fact]
    public async Task Void_last_valuation_without_purchase_is_422_value_required()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app, Body(app, purchasePrice: null, purchasedOn: null, currentValue: "800"));
        var valuation = (await GetAsync(app, id)).GetProperty("valuations")[0].GetProperty("id").GetGuid();

        await AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Route}/{id.Value}/valuations/{valuation}"),
            HttpStatusCode.UnprocessableEntity, AssetsCodes.ValueRequired);
        Assert.Single((await GetAsync(app, id)).GetProperty("valuations").EnumerateArray());
    }

    [Fact]
    public async Task Void_last_valuation_with_purchase_falls_back_to_purchase()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app, Body(app));
        var valuation = (await JsonAsync(await RecordAsync(app, id, "2025-06-01", "1500"))).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Route}/{id.Value}/valuations/{valuation}")).StatusCode);

        var view = await GetAsync(app, id);
        Assert.True(view.GetProperty("valueFromPurchase").GetBoolean());
        Assert.Equal("1000.00", view.GetProperty("value").GetProperty("amount").GetString());
    }

    [Fact]
    public async Task Valuation_on_a_property_id_is_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var property = await PropertyApi.NewPropertyAsync(app, PropertyApi.Home(app));

        Assert.Equal(HttpStatusCode.NotFound,
            (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Route}/{property}/valuations", new { asOf = "2025-06-01", amount = "1500" })).StatusCode);
    }
}
