using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.Assets.IntegrationTests.AssetApi;

namespace Nid.Assets.IntegrationTests;

/// <summary>#650: a vehicle's mileage readings are listed on the asset, and a wrong one is removed.</summary>
public class MileageReadingTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<ItemId> CarAsync(NidApp app)
    {
        var body = Body(app, "Citadine", "vehicle");
        body["mileageKm"] = 12_000;
        body["mileageOn"] = "2025-03-01";
        var id = await CreateAsync(app, body);
        var put = new Dictionary<string, object?>
        {
            ["name"] = "Citadine", ["kind"] = "vehicle", ["purchasePrice"] = "1000", ["purchasedOn"] = "2025-01-10",
            ["mileageKm"] = 150_000, ["mileageOn"] = "2025-09-01", ["version"] = (await GetAsync(app, id)).GetProperty("version").GetUInt32(),
        };
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Route}/{id.Value}", put)).StatusCode);
        return id;
    }

    private static IEnumerable<(string?, int)> Readings(JsonElement view)
        => view.GetProperty("mileageReadings").EnumerateArray().Select(r => (r.GetProperty("on").GetString(), r.GetProperty("km").GetInt32()));

    [Fact]
    public async Task A_vehicle_lists_its_readings_and_a_wrong_one_is_removed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CarAsync(app);
        Assert.Equal([("2025-03-01", 12_000), ("2025-09-01", 150_000)], Readings(await GetAsync(app, id)));

        var response = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Route}/{id.Value}/mileage-readings/2025-09-01");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var view = await GetAsync(app, id);
        Assert.Equal([("2025-03-01", 12_000)], Readings(view));
        Assert.Equal((12_000, "2025-03-01"), (view.GetProperty("mileageKm").GetInt32(), view.GetProperty("mileageOn").GetString()));
    }

    [Fact]
    public async Task A_day_without_a_reading_is_not_found()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CarAsync(app);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Route}/{id.Value}/mileage-readings/2025-05-05");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_malformed_day_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CarAsync(app);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Route}/{id.Value}/mileage-readings/yesterday");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }
}
