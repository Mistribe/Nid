using System.Net;
using System.Text.Json;
using Nid.Assets.Contracts;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.Assets.IntegrationTests.AssetApi;

namespace Nid.Assets.IntegrationTests;

/// <summary>#649: a vehicle's running costs, Core flows of the asset. Today is <see cref="NidApp.GoldenCaseToday"/> (2025-10-15).</summary>
public class AssetCostTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static Dictionary<string, object?> Car(NidApp app) => Body(app, "Citadine", "vehicle");

    private static Task<HttpResponseMessage> AddAsync(NidApp app, ItemId id, string occurredOn = "2025-06-01", string amount = "120", string category = "service",
        object? extra = null, PersonId? actor = null)
        => app.RawAsync(actor ?? app.AdultA, HttpMethod.Post, $"{Route}/{id.Value}/costs", new Dictionary<string, object?>
        {
            ["occurredOn"] = occurredOn, ["amount"] = amount, ["category"] = category, ["note"] = "Révision",
            ["documentId"] = extra,
        });

    private static async Task<JsonElement> CostsAsync(NidApp app, ItemId id, string query = "")
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Route}/{id.Value}/costs{query}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await JsonAsync(response);
    }

    [Fact]
    public async Task Record_correct_then_void_a_cost()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app, Car(app));

        var recorded = await AddAsync(app, id);
        Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);
        var flow = (await JsonAsync(recorded)).GetProperty("id").GetGuid();
        var row = (await CostsAsync(app, id)).GetProperty("rows")[0];
        Assert.Equal(("service", "120.00", "Révision", "2025-06-01"),
            (row.GetProperty("category").GetString(), row.GetProperty("amount").GetProperty("amount").GetString(), row.GetProperty("note").GetString(), row.GetProperty("occurredOn").GetString()));

        var corrected = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Route}/{id.Value}/costs/{flow}",
            new { occurredOn = "2025-06-02", amount = "150.50", category = "repair", note = "Embrayage" });
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        flow = (await JsonAsync(corrected)).GetProperty("id").GetGuid(); // a correction is a new flow superseding the old one
        var view = await CostsAsync(app, id);
        Assert.Equal(1, view.GetProperty("rows").GetArrayLength());
        Assert.Equal(("repair", "150.50"), (view.GetProperty("rows")[0].GetProperty("category").GetString(), view.GetProperty("total").GetProperty("amount").GetString()));

        var voided = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Route}/{id.Value}/costs/{flow}");
        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        Assert.Empty((await CostsAsync(app, id)).GetProperty("rows").EnumerateArray());
    }

    [Fact]
    public async Task A_cost_on_a_non_vehicle_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var art = await CreateAsync(app, Body(app));

        await AssertRefusedAsync(await AddAsync(app, art), HttpStatusCode.UnprocessableEntity, AssetsCodes.CostsVehicleOnly);
    }

    [Fact]
    public async Task A_cost_is_checked_on_its_fields()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app, Car(app));

        await AssertRefusedAsync(await AddAsync(app, id, category: "fuel"), HttpStatusCode.UnprocessableEntity, AssetsCodes.InvalidCategory, "category");
        await AssertRefusedAsync(await AddAsync(app, id, amount: "0"), HttpStatusCode.UnprocessableEntity, AssetsCodes.InvalidValue, "amount");
        await AssertRefusedAsync(await AddAsync(app, id, occurredOn: "2025-10-16"), HttpStatusCode.UnprocessableEntity, CoreFactCodes.DateInFuture, "occurredOn");
        await AssertRefusedAsync(
            await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Route}/{id.Value}/costs", new { occurredOn = "2025-06-01", amount = "10", category = "service", note = new string('x', 2001) }),
            HttpStatusCode.UnprocessableEntity, "request.invalid", "note");
        Assert.Empty((await CostsAsync(app, id)).GetProperty("rows").EnumerateArray());
    }

    [Fact]
    public async Task The_year_total_is_the_servers_and_defaults_to_the_current_year()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app, Car(app));
        Assert.Equal(HttpStatusCode.Created, (await AddAsync(app, id, "2025-03-01", "100.10")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AddAsync(app, id, "2025-09-01", "200.20", "tyres")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AddAsync(app, id, "2024-05-01", "50", "other")).StatusCode);

        var now = await CostsAsync(app, id);
        Assert.Equal((2025, "300.30", 2), (now.GetProperty("year").GetInt32(), now.GetProperty("total").GetProperty("amount").GetString(), now.GetProperty("rows").GetArrayLength()));
        Assert.Equal("2025-09-01", now.GetProperty("rows")[0].GetProperty("occurredOn").GetString());
        Assert.Equal([2025, 2024], now.GetProperty("years").EnumerateArray().Select(y => y.GetInt32()));

        var before = await CostsAsync(app, id, "?year=2024");
        Assert.Equal(("50.00", 1), (before.GetProperty("total").GetProperty("amount").GetString(), before.GetProperty("rows").GetArrayLength()));
    }

    [Fact]
    public async Task An_invoice_is_attached_kept_on_correction_and_shown_to_its_readers()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app, Car(app));
        var invoice = await DocumentApi.UploadedAsync(app, app.AdultA, DocumentApi.Pdf, "fileName=facture.pdf");

        var flow = (await JsonAsync(await AddAsync(app, id, extra: invoice))).GetProperty("id").GetGuid();
        Assert.Equal(invoice, (await CostsAsync(app, id)).GetProperty("rows")[0].GetProperty("documentId").GetGuid());

        // A correction without a document keeps the invoice.
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Route}/{id.Value}/costs/{flow}",
            new { occurredOn = "2025-06-01", amount = "130", category = "service" })).StatusCode);
        Assert.Equal(invoice, (await CostsAsync(app, id)).GetProperty("rows")[0].GetProperty("documentId").GetGuid());

        // B reads the vehicle (shared) but not A's private document: the cost shows, the invoice does not.
        var seen = await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Route}/{id.Value}/costs");
        Assert.Equal(JsonValueKind.Null, (await JsonAsync(seen)).GetProperty("rows")[0].GetProperty("documentId").ValueKind);
    }

    [Fact]
    public async Task An_unreadable_invoice_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app, Car(app));
        var theirs = await DocumentApi.UploadedAsync(app, app.AdultB, DocumentApi.Pdf, "fileName=facture-b.pdf");

        Assert.Equal(HttpStatusCode.NotFound, (await AddAsync(app, id, extra: theirs)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AddAsync(app, id, extra: Guid.NewGuid())).StatusCode);
        Assert.Empty((await CostsAsync(app, id)).GetProperty("rows").EnumerateArray());
    }

    [Fact]
    public async Task A_reader_who_cannot_write_cannot_record()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app, Car(app));

        var refused = await AddAsync(app, id, actor: app.AdultB);

        Assert.True(refused.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
        Assert.Empty((await CostsAsync(app, id)).GetProperty("rows").EnumerateArray());
    }

    [Fact]
    public async Task A_cost_on_a_property_id_is_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var property = await PropertyApi.NewPropertyAsync(app, PropertyApi.Home(app));

        Assert.Equal(HttpStatusCode.NotFound,
            (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Route}/{property}/costs", new { occurredOn = "2025-06-01", amount = "10", category = "service" })).StatusCode);
    }

    [Fact]
    public async Task A_vehicle_turned_into_another_kind_keeps_its_costs_and_can_void_them_but_takes_no_new_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app, Car(app));
        var flow = (await JsonAsync(await AddAsync(app, id))).GetProperty("id").GetGuid();
        var asset = await GetAsync(app, id);
        var edit = Body(app, "Citadine", "art");
        edit.Remove("currentValue"); edit.Remove("currentValueOn"); edit.Remove("visibility"); edit.Remove("owners");
        edit["version"] = asset.GetProperty("version").GetUInt32();
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Route}/{id.Value}", edit)).StatusCode);

        Assert.Equal(1, (await CostsAsync(app, id)).GetProperty("rows").GetArrayLength());
        await AssertRefusedAsync(await AddAsync(app, id), HttpStatusCode.UnprocessableEntity, AssetsCodes.CostsVehicleOnly);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Route}/{id.Value}/costs/{flow}")).StatusCode);
    }
}
