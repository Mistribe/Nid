using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Host.IntegrationTests.Import;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Export;

/// <summary>Other assets (#186): assets and their valuations, exported, imported into a fresh instance, export the same again.</summary>
public class AssetsRoundTripTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task NameAdultsAsync(NidApp app)
    {
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        (await core.Persons.SingleAsync(p => p.Id == app.AdultA.Value)).DisplayName = "Adulte 1";
        (await core.Persons.SingleAsync(p => p.Id == app.AdultB.Value)).DisplayName = "Adulte 2";
        await core.SaveChangesAsync();
    }

    [Fact]
    public async Task Export_then_import_round_trips_assets()
    {
        await using var source = await NidApp.StartAsync(fixture);
        await NameAdultsAsync(source);
        string[] assets =
        [
            """{"id":"asset-car","name":"Voiture","kind":"vehicle","currency":"EUR","purchase_price":"15000.00","purchased_on":"2023-05-01","note":"Citadine","registered_on":"2022-03-01","mileage_km":45000,"mileage_on":"2025-06-01","fiscal_hp":5,"energy":"diesel","ownership":[{"person":"adult-1","share_bp":10000}]}""",
            """{"id":"asset-gold","name":"Lingot","kind":"precious_metal","currency":"EUR","weight_grams":"100.000","fineness":"999.9","closed":true,"ownership":[{"person":"adult-1","share_bp":6000},{"person":"adult-2","share_bp":4000}]}""",
        ];
        await RoundTripTests.CommitAsync(source, source.AdultA, AssetsImportTests.File(assets,
            """{"id":"v-gold","item":"asset-gold","as_of":"2025-06-01","amount":{"amount":"6000.00","currency":"EUR"}}"""));

        var first = await new ExportClient(source).ExportAllAsync(source.AdultA);

        await using var target = await NidApp.StartAsync(fixture);
        await NameAdultsAsync(target);
        await RoundTripTests.CommitAsync(target, target.AdultA, first.Where(s => s.Value.Count > 0).ToDictionary(s => s.Key, s => JsonSerializer.Serialize(s.Value)));
        var second = await new ExportClient(target).ExportAllAsync(target.AdultA);

        Assert.Equal(RoundTripTests.Canonical(first), RoundTripTests.Canonical(second));
        var gold = first["assets"].Single(a => a.GetProperty("name").GetString() == "Lingot");
        Assert.Equal(("999.9", true), (gold.GetProperty("fineness").GetString(), gold.GetProperty("closed").GetBoolean()));
        var car = first["assets"].Single(a => a.GetProperty("name").GetString() == "Voiture");
        Assert.Equal(("2022-03-01", 45000, "diesel"), (car.GetProperty("registered_on").GetString(), car.GetProperty("mileage_km").GetInt32(), car.GetProperty("energy").GetString()));
        Assert.Single(first["valuations"]);
    }
}
