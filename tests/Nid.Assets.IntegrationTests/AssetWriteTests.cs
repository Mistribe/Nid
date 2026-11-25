using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Assets.Application;
using Nid.Assets.Contracts;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.Assets.IntegrationTests.AssetApi;

namespace Nid.Assets.IntegrationTests;

/// <summary>The asset writes. Today is <see cref="NidApp.GoldenCaseToday"/> (2025-10-15).</summary>
public class AssetWriteTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static Dictionary<string, object?> Edit(uint version, string name = "Tableau", string kind = "art", string? price = "1000",
        string? on = "2025-01-10", decimal? weight = null, decimal? fineness = null) => new()
    {
        ["name"] = name, ["kind"] = kind, ["purchasePrice"] = price, ["purchasedOn"] = on, ["note"] = null, ["weightGrams"] = weight,
        ["fineness"] = fineness, ["version"] = version,
    };

    private static async Task<uint> VersionAsync(NidApp app, ItemId id) => (await GetAsync(app, id)).GetProperty("version").GetUInt32();

    [Fact]
    public async Task Create_with_purchase_only_values_it_at_purchase()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var view = await GetAsync(app, await CreateAsync(app, Body(app)));

        Assert.True(view.GetProperty("valueFromPurchase").GetBoolean());
        Assert.Equal("1000.00", view.GetProperty("value").GetProperty("amount").GetString());
        Assert.Equal("2025-01-10", view.GetProperty("valueOn").GetString());
        Assert.Empty(view.GetProperty("valuations").EnumerateArray());
    }

    [Fact]
    public async Task Create_with_current_value_only_records_a_valuation()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var view = await GetAsync(app, await CreateAsync(app, Body(app, purchasePrice: null, purchasedOn: null, currentValue: "800")));

        Assert.False(view.GetProperty("valueFromPurchase").GetBoolean());
        Assert.Equal("800.00", view.GetProperty("value").GetProperty("amount").GetString());
        Assert.Equal("2025-10-15", view.GetProperty("valueOn").GetString());
        Assert.Equal("800.00", Assert.Single(view.GetProperty("valuations").EnumerateArray()).GetProperty("amount").GetString());
    }

    [Fact]
    public async Task Create_with_a_future_value_date_is_422_and_leaves_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);

        await AssertRefusedAsync(
            await app.RawAsync(app.AdultA, HttpMethod.Post, Route, Body(app, purchasePrice: null, purchasedOn: null, currentValue: "800", currentValueOn: "2030-01-01")),
            HttpStatusCode.UnprocessableEntity, "core.date_in_future", "currentValueOn");

        var list = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, Route));
        Assert.Equal(0, list.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Create_with_a_value_date_but_no_value_is_422_on_currentValueOn()
    {
        await using var app = await NidApp.StartAsync(fixture);

        await AssertRefusedAsync(
            await app.RawAsync(app.AdultA, HttpMethod.Post, Route, Body(app, currentValueOn: "2025-02-01")),
            HttpStatusCode.UnprocessableEntity, AssetsCodes.InvalidValue, "currentValueOn");
    }

    [Fact]
    public async Task Create_without_any_value_is_422_value_required()
    {
        await using var app = await NidApp.StartAsync(fixture);

        await AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Route, Body(app, purchasePrice: null, purchasedOn: null)),
            HttpStatusCode.UnprocessableEntity, AssetsCodes.ValueRequired);
    }

    [Fact]
    public async Task Create_with_price_without_date_is_422_purchase_incomplete_on_purchasedOn()
    {
        await using var app = await NidApp.StartAsync(fixture);

        await AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Route, Body(app, purchasedOn: null)),
            HttpStatusCode.UnprocessableEntity, AssetsCodes.PurchaseIncomplete, "purchasedOn");
    }

    [Fact]
    public async Task Edit_from_metal_to_art_clears_weight_and_fineness()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Body(app, "Lingot", "precious_metal");
        body["weightGrams"] = 31.103m;
        body["fineness"] = 999.9m;
        var id = await CreateAsync(app, body);
        var created = await GetAsync(app, id);
        Assert.Equal(31.103m, created.GetProperty("weightGrams").GetDecimal());

        var edited = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Route}/{id.Value}", Edit(await VersionAsync(app, id), "Lingot", "art"));

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var view = await GetAsync(app, id);
        Assert.Equal("art", view.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("weightGrams").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("fineness").ValueKind);
    }

    private static void Vehicle(Dictionary<string, object?> body, int km = 45_000, string kmOn = "2025-10-01")
    {
        body["registeredOn"] = "2020-05-01";
        body["mileageKm"] = km;
        body["mileageOn"] = kmOn;
        body["fiscalHp"] = 5;
        body["energy"] = "hybrid";
    }

    [Fact]
    public async Task Vehicle_fields_round_trip_and_a_mileage_update_keeps_the_latest_reading()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Body(app, "Citadine", "vehicle");
        Vehicle(body);
        var id = await CreateAsync(app, body);
        var view = await GetAsync(app, id);
        Assert.Equal(("2020-05-01", 45_000, "2025-10-01", 5, "hybrid"), (view.GetProperty("registeredOn").GetString(), view.GetProperty("mileageKm").GetInt32(),
            view.GetProperty("mileageOn").GetString(), view.GetProperty("fiscalHp").GetInt32(), view.GetProperty("energy").GetString()));

        var edit = Edit(await VersionAsync(app, id), "Citadine", "vehicle");
        Vehicle(edit, 51_000, "2025-10-14");
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Route}/{id.Value}", edit)).StatusCode);

        view = await GetAsync(app, id);
        Assert.Equal((51_000, "2025-10-14"), (view.GetProperty("mileageKm").GetInt32(), view.GetProperty("mileageOn").GetString()));
    }

    [Fact]
    public async Task Vehicle_fields_on_art_are_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Body(app, "Tableau", "art");
        body["energy"] = "diesel";

        await AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Route, body), HttpStatusCode.UnprocessableEntity, AssetsCodes.VehicleOnly, "energy");
    }

    [Fact]
    public async Task Mileage_without_its_date_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Body(app, "Citadine", "vehicle");
        body["mileageKm"] = 100;

        await AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Route, body), HttpStatusCode.UnprocessableEntity, AssetsCodes.MileageIncomplete, "mileageOn");
    }

    [Fact]
    public async Task Edit_from_vehicle_to_art_clears_the_vehicle_fields()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Body(app, "Citadine", "vehicle");
        Vehicle(body);
        var id = await CreateAsync(app, body);

        var edited = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Route}/{id.Value}", Edit(await VersionAsync(app, id), "Citadine", "art"));

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var view = await GetAsync(app, id);
        foreach (var f in new[] { "registeredOn", "mileageKm", "mileageOn", "fiscalHp", "energy" })
            Assert.Equal(JsonValueKind.Null, view.GetProperty(f).ValueKind);
    }

    [Fact]
    public async Task Edit_removing_price_without_valuation_is_422()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app, Body(app));

        await AssertRefusedAsync(
            await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Route}/{id.Value}", Edit(await VersionAsync(app, id), price: null, on: null)),
            HttpStatusCode.UnprocessableEntity, AssetsCodes.ValueRequired);
        var view = await GetAsync(app, id);
        Assert.Equal("1000.00", view.GetProperty("purchasePrice").GetProperty("amount").GetString());
        Assert.Equal("2025-01-10", view.GetProperty("purchasedOn").GetString());
    }

    [Fact]
    public async Task Edit_with_stale_version_is_409()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app, Body(app));
        var version = await VersionAsync(app, id);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Route}/{id.Value}", Edit(version, "Premier"))).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Route}/{id.Value}", Edit(version, "Second"))).StatusCode);
    }

    [Fact]
    public async Task Archive_then_unarchive()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app, Body(app));

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Route}/{id.Value}/archive")).StatusCode);
        Assert.True((await GetAsync(app, id)).GetProperty("archived").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Route}/{id.Value}/unarchive")).StatusCode);
        Assert.False((await GetAsync(app, id)).GetProperty("archived").GetBoolean());
    }

    [Fact]
    public async Task Delete_removes_item_and_row()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app, Body(app));
        var preview = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Route}/{id.Value}/deletion"));

        var deleted = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Route}/{id.Value}?version={preview.GetProperty("version").GetUInt32()}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Route}/{id.Value}")).StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IAssetRecordStore>().FindAsync(id, TestContext.Current.CancellationToken));
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IItemRegistry>().FindAsync(id, TestContext.Current.CancellationToken));
    }
}
