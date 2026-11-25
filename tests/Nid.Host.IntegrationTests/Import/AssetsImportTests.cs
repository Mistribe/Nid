using System.Text.Json;
using Nid.Assets.Application;
using Nid.Assets.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using static Nid.Host.IntegrationTests.Import.EnvelopesImportTests;

namespace Nid.Host.IntegrationTests.Import;

/// <summary>Other assets (#186): the <c>assets</c> section, its valuations through the Core's <c>valuations</c>, asserted through the API.</summary>
public class AssetsImportTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string OwnerA = """[{"person":"adult-1","share_bp":10000}]""";

    private const string Car = $$"""
        {"id":"asset-car","name":"Voiture","kind":"vehicle","currency":"EUR","purchase_price":"15000.00","purchased_on":"2023-05-01",
         "note":"Citadine","registered_on":"2022-03-01","mileage_km":45000,"mileage_on":"2025-06-01","fiscal_hp":5,"energy":"diesel","depreciation_rate":"12.50","ownership":{{OwnerA}}}
        """;

    private const string Gold = $$"""
        {"id":"asset-gold","name":"Lingot","kind":"precious_metal","currency":"EUR","weight_grams":"100.000","fineness":"999.9",
         "ownership":{{OwnerA}}}
        """;

    private const string GoldValue = """
        {"id":"v-gold","item":"asset-gold","as_of":"2025-06-01","amount":{"amount":"6000.00","currency":"EUR"}}
        """;

    internal static Dictionary<string, string> File(string[] assets, params string[] valuations)
    {
        var file = new Dictionary<string, string> { ["persons"] = GoldenFile.Persons, ["assets"] = "[" + string.Join(",", assets) + "]" };
        if (valuations.Length > 0)
            file["valuations"] = "[" + string.Join(",", valuations) + "]";
        return file;
    }

    private static IEnumerable<(string Code, string Path)> ErrorsOf(SessionResponse session)
        => session.Report!.Issues.Where(i => i.Severity == "error").Select(i => (i.Code, i.Path));

    private static async Task<ItemId> IdOfAsync(NidApp app, string fileId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var known = await scope.ServiceProvider.GetRequiredService<IAssetRecordStore>().FindImportedAsync([fileId], default);
        return known[fileId].Single().Item;
    }

    private static async Task<JsonElement> ReadAsync(NidApp app, ItemId id, PersonId? reader = null)
        => await JsonAsync(app, reader ?? app.AdultA, $"/api/v1/assets/{id.Value}");

    [Fact]
    public async Task Import_creates_assets_and_their_valuations()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var committed = await CommitAsync(new ImportClient(app), app, app.AdultA, File([Car, Gold], GoldValue));

        Assert.Equal(new CountsResponse(2, 0, 0), committed.Report!.Sections["assets"]);
        Assert.Equal(new CountsResponse(1, 0, 0), committed.Report.Sections["valuations"]);
        var car = await ReadAsync(app, await IdOfAsync(app, "asset-car"));
        Assert.Equal(("Voiture", "vehicle", "EUR", "15000.00", "2023-05-01", "Citadine"),
            (car.GetProperty("name").GetString(), car.GetProperty("kind").GetString(), car.GetProperty("currency").GetString(),
             car.GetProperty("purchasePrice").GetProperty("amount").GetString(), car.GetProperty("purchasedOn").GetString(), car.GetProperty("note").GetString()));
        Assert.Equal(("2022-03-01", 45_000, "2025-06-01", 5, "diesel"), (car.GetProperty("registeredOn").GetString(), car.GetProperty("mileageKm").GetInt32(),
            car.GetProperty("mileageOn").GetString(), car.GetProperty("fiscalHp").GetInt32(), car.GetProperty("energy").GetString()));
        Assert.Equal(12.5m, car.GetProperty("depreciationRate").GetDecimal());
        var gold = await ReadAsync(app, await IdOfAsync(app, "asset-gold"));
        Assert.Equal((100m, 999.9m, "6000.00"),
            (gold.GetProperty("weightGrams").GetDecimal(), gold.GetProperty("fineness").GetDecimal(), gold.GetProperty("value").GetProperty("amount").GetString()));
        Assert.Single(gold.GetProperty("valuations").EnumerateArray());
    }

    [Fact]
    public async Task Reimport_updates_not_duplicates()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, File([Car, Gold], GoldValue));
        var car = await IdOfAsync(app, "asset-car");

        var again = await CommitAsync(client, app, app.AdultA, File([Car, Gold], GoldValue));

        Assert.Equal(new CountsResponse(0, 2, 0), again.Report!.Sections["assets"]);
        var changed = File([Car.Replace("\"Voiture\"", "\"Voiture de Léa\"").Replace("15000.00", "14000.00"), Gold], GoldValue);
        var conflict = await ValidateAsync(client, app, app.AdultA, changed);
        Assert.Equal([(ImportCodes.Conflict, "assets[0]")], ErrorsOf(conflict));
        await client.CancelRawAsync(app.AdultA, conflict.Id);

        var replaced = await CommitAsync(client, app, app.AdultA, changed, replace: true);

        Assert.Equal(new CountsResponse(0, 1, 0, 1), replaced.Report!.Sections["assets"]);
        Assert.Equal(car, await IdOfAsync(app, "asset-car"));
        var read = await ReadAsync(app, car);
        Assert.Equal(("Voiture de Léa", "14000.00"), (read.GetProperty("name").GetString(), read.GetProperty("purchasePrice").GetProperty("amount").GetString()));
        var list = await JsonAsync(app, app.AdultA, "/api/v1/assets");
        Assert.Equal(2, list.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task An_income_flow_on_an_asset_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var file = File([Car]);
        file["flows"] = """[{"id":"f-car","item":"asset-car","occurred_on":"2024-01-01","kind":"income","amount":{"amount":"100","currency":"EUR"},"category":"service"}]""";

        var session = await ValidateAsync(new ImportClient(app), app, app.AdultA, file);

        Assert.Equal([(AssetsCodes.InvalidCategory, "flows[0].kind")], ErrorsOf(session));
    }

    [Fact]
    public async Task A_cost_without_a_known_category_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var file = File([Car]);
        file["flows"] = """[{"id":"f-car","item":"asset-car","occurred_on":"2024-01-01","kind":"expense","amount":{"amount":"100","currency":"EUR"},"category":"fuel"}]""";

        var session = await ValidateAsync(new ImportClient(app), app, app.AdultA, file);

        Assert.Equal([(AssetsCodes.InvalidCategory, "flows[0].category")], ErrorsOf(session));
    }

    [Fact]
    public async Task A_vehicle_cost_travels_in_the_flows_section_and_back()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var file = File([Car]);
        file["flows"] = """[{"id":"f-service","item":"asset-car","occurred_on":"2024-02-01","kind":"expense","amount":{"amount":"120.50","currency":"EUR"},"category":"service","note":"Révision"}]""";

        var committed = await CommitAsync(new ImportClient(app), app, app.AdultA, file);

        Assert.Equal(new CountsResponse(1, 0, 0), committed.Report!.Sections["flows"]);
        var costs = await JsonAsync(app, app.AdultA, $"/api/v1/assets/{(await IdOfAsync(app, "asset-car")).Value}/costs?year=2024");
        Assert.Equal(("service", "120.50", "120.50"), (costs.GetProperty("rows")[0].GetProperty("category").GetString(),
            costs.GetProperty("rows")[0].GetProperty("amount").GetProperty("amount").GetString(), costs.GetProperty("total").GetProperty("amount").GetString()));
        var export = await new Export.ExportClient(app).ExportAllAsync(app.AdultA);
        var flow = Assert.Single(export["flows"]);
        Assert.Equal(("expense", "service", export["assets"][0].GetProperty("id").GetString()),
            (flow.GetProperty("kind").GetString(), flow.GetProperty("category").GetString(), flow.GetProperty("item").GetString()));
    }

    [Fact]
    public async Task Weight_on_vehicle_is_refused_on_its_row()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA, Adults(app));

        var batch = await client.PutAsync(app.AdultA, session.Id, "assets", 0, "[" + Car.Replace("\"note\"", "\"weight_grams\":\"10\",\"note\"") + "]");

        Assert.False(batch.Accepted);
        Assert.Equal([(AssetsCodes.MetalOnly, "assets[0].weight_grams")], batch.Issues.Select(i => (i.Code, i.Path)));
    }

    [Fact]
    public async Task Vehicle_fields_on_a_work_of_art_are_refused_on_their_row()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA, Adults(app));

        var batch = await client.PutAsync(app.AdultA, session.Id, "assets", 0, "[" + Car.Replace("\"vehicle\"", "\"art\"") + "]");

        Assert.False(batch.Accepted);
        Assert.Equal([(AssetsCodes.VehicleOnly, "assets[0].registered_on")], batch.Issues.Select(i => (i.Code, i.Path)));
    }

    [Fact]
    public async Task Valuation_naming_unknown_asset_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var session = await ValidateAsync(new ImportClient(app), app, app.AdultA, File([Car], GoldValue));

        Assert.Equal("invalid", session.State);
        Assert.Equal([(ImportCodes.UnresolvedReference, "valuations[0].item")], ErrorsOf(session));
    }

    [Fact]
    public async Task Asset_without_any_value_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var session = await ValidateAsync(new ImportClient(app), app, app.AdultA, File([Car, Gold]));

        Assert.Equal("invalid", session.State);
        Assert.Equal([(AssetsCodes.ValueRequired, "assets[1]")], ErrorsOf(session));
    }

    [Fact]
    public async Task Purchase_price_without_date_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA, Adults(app));

        var batch = await client.PutAsync(app.AdultA, session.Id, "assets", 0, "[" + Car.Replace(",\"purchased_on\":\"2023-05-01\"", "") + "]");

        Assert.False(batch.Accepted);
        Assert.Equal([(AssetsCodes.PurchaseIncomplete, "assets[0].purchased_on")], batch.Issues.Select(i => (i.Code, i.Path)));
    }

    [Fact]
    public async Task Private_asset_of_alice_not_in_bob_export()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var secret = Car.Replace("\"Voiture\"", "\"ZZSECRETASSET\"").Replace("asset-car", "asset-secret").Replace("\"ownership\"", "\"visibility\":\"private\",\"ownership\"");
        await CommitAsync(new ImportClient(app), app, app.AdultA, File([Car, secret]));

        var bob = await new Export.ExportClient(app).ExportAllAsync(app.AdultB);
        var alice = await new Export.ExportClient(app).ExportAllAsync(app.AdultA);

        Assert.Equal(["Voiture"], bob["assets"].Select(a => a.GetProperty("name").GetString()));
        Assert.DoesNotContain("ZZSECRETASSET", JsonSerializer.Serialize(bob), StringComparison.Ordinal);
        Assert.Equal(2, alice["assets"].Count);
    }
}
