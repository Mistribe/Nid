using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.Subscriptions.IntegrationTests.RealEstate.LoanApi;
using static Nid.Subscriptions.IntegrationTests.RealEstate.PropertyApi;

namespace Nid.Subscriptions.IntegrationTests.RealEstate;

/// <summary>Spec 3C1 §4 and §7: the property's reads, creation, PATCH, and the 404s.</summary>
[Collection("postgres")]
public class PropertyEndpointTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_property_is_created_and_read_back_with_its_figures_before_any_estimate() // Review Focus #2: amounts go out invariant
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Home(app);
        body["surfaceM2"] = "85.5";
        body["locationLabel"] = "Nantes";
        body["note"] = "achat";
        var id = await NewPropertyAsync(app, body);

        var detail = await PropertyAsync(app, id);
        Assert.Equal(("Maison", "primary_residence", "EUR", "shared"), (S(detail, "name"), S(detail, "type"), S(detail, "currency"), S(detail, "visibility")));
        Assert.Equal(app.AdultA.Value, Only(detail, "owners").GetProperty("personId").GetGuid());
        Assert.Equal(("2020-06-15", "250000.00", "19000.00", "0.00", "0.00"),
            (S(detail, "purchaseDate"), S(detail, "purchasePrice"), S(detail, "notaryFees"), S(detail, "agencyFees"), S(detail, "otherAcquisitionCosts")));
        Assert.Equal(("85.50", "Nantes", "yearly", "achat"), (S(detail, "surfaceM2"), S(detail, "locationLabel"), S(detail, "valuationCadence"), S(detail, "note")));
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("staleAfterDays").ValueKind);
        Assert.NotEqual(0u, detail.GetProperty("version").GetUInt32());
        Assert.True(detail.GetProperty("canWrite").GetBoolean());

        var figures = detail.GetProperty("figures");
        Assert.Equal(JsonValueKind.Null, figures.GetProperty("value").ValueKind); // unknown: never 0 nor the purchase price
        Assert.Equal(JsonValueKind.Null, figures.GetProperty("latentGain").ValueKind);
        Assert.Equal(JsonValueKind.Null, figures.GetProperty("netValue").ValueKind);
        Assert.Equal(("269000.00", "0.00", "269000.00", "269000.00"),
            (S(figures, "costPrice"), S(figures, "linkedBalance"), S(figures, "realCost"), S(figures, "netCostOfRents")));
        Assert.Equal(["acquisition", "loan_interest", "loan_insurance", "loan_fees"],
            figures.GetProperty("realCostLines").EnumerateArray().Select(l => S(l, "code")));
        Assert.Equal("no_valuation", S(Only(detail, "warnings"), "code"));
        Assert.Empty(detail.GetProperty("valuations").EnumerateArray());
        Assert.Empty(detail.GetProperty("flows").EnumerateArray());
        Assert.Empty(detail.GetProperty("loans").EnumerateArray());

        var row = Assert.Single((await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Properties}?scope=household"))).GetProperty("items").EnumerateArray());
        Assert.Equal((id, "Maison", "primary_residence", "EUR"), (row.GetProperty("id").GetGuid(), S(row, "name"), S(row, "type"), S(row, "currency")));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("netValue").ValueKind);
        Assert.False(row.GetProperty("stale").GetBoolean());
        Assert.True(row.GetProperty("canWrite").GetBoolean());

        // Shared: B reads it and cannot change it.
        Assert.False((await PropertyAsync(app, id, app.AdultB)).GetProperty("canWrite").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Patch, $"{Properties}/{id}",
            new { version = detail.GetProperty("version").GetUInt32(), note = "x" })).StatusCode);

        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var item = await core.Items.SingleAsync(i => i.Id == id);
        Assert.Equal(("realestate", "property", "EUR"), (item.Module, item.Kind, item.Currency!.Trim()));
    }

    [Fact]
    public async Task Each_creation_refusal_answers_at_the_bodys_own_path() // Review Focus #2 for the French comma
    {
        await using var app = await NidApp.StartAsync(fixture);
        const string Field = "realestate.invalid_property_field";
        const HttpStatusCode Unprocessable = HttpStatusCode.UnprocessableEntity;
        var cases = new (string Why, Action<Dictionary<string, object?>> Change, HttpStatusCode Status, string? Code, string? Path)[]
        {
            ("no visibility", b => b.Remove("visibility"), Unprocessable, "request.visibility", "visibility"),
            ("no owner", b => b.Remove("owners"), Unprocessable, "request.required", "owners"),
            ("a lowercase currency", b => b["currency"] = "eur", Unprocessable, "core.unknown_currency", "currency"),
            ("no type", b => b.Remove("type"), Unprocessable, "request.required", "type"),
            ("a castle", b => b["type"] = "castle", Unprocessable, Field, "type"),
            ("a cadence outside the list", b => b["valuationCadence"] = "monthly", Unprocessable, Field, "valuationCadence"),
            ("a French decimal comma", b => b["purchasePrice"] = "250000,50", Unprocessable, "request.amount", "purchasePrice"),
            ("a third decimal", b => b["purchasePrice"] = "250000.005", Unprocessable, Field, "purchasePrice"),
            ("a free purchase", b => b["purchasePrice"] = "0", Unprocessable, Field, "purchasePrice"),
            ("a future purchase", b => b["purchaseDate"] = "2025-10-16", Unprocessable, Field, "purchaseDate"),
            ("a purchase in 1899", b => b["purchaseDate"] = "1899-12-31", Unprocessable, Field, "purchaseDate"),
            ("negative fees", b => b["notaryFees"] = "-1", Unprocessable, Field, "notaryFees"),
            ("no surface", b => b["surfaceM2"] = "0", Unprocessable, Field, "surfaceM2"),
            ("a full address", b => b["locationLabel"] = new string('x', 101), Unprocessable, Field, "locationLabel"),
            ("a threshold of 3651 days", b => b["staleAfterDays"] = 3651, Unprocessable, Field, "staleAfterDays"),
            ("a rental start before the purchase", b => b["rentalStartsOn"] = "2020-06-14", Unprocessable, Field, "rentalStartsOn"),
            ("a long note", b => b["note"] = new string('x', 2001), Unprocessable, Field, "note"),
            ("a blank name", b => b["name"] = "  ", Unprocessable, Field, "name"),
            ("a private property with two owners", b =>
                {
                    b["visibility"] = "private";
                    b["owners"] = new[] { new { personId = app.AdultA.Value, shareBp = 5_000 }, new { personId = app.AdultB.Value, shareBp = 5_000 } };
                }, Unprocessable, "core.private_single_owner", "owners"),
            ("the other adult's property alone", b => b["owners"] = new[] { new { personId = app.AdultB.Value, shareBp = 10_000 } },
                HttpStatusCode.Forbidden, null, null),
        };

        foreach (var (why, change, status, code, path) in cases)
        {
            var body = Home(app);
            change(body);
            var (gotStatus, gotCode, gotPath) = await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Properties, body));
            Assert.True(gotStatus == status && (code is null || (gotCode, gotPath) == (code, path)),
                $"{why}: expected {status} {code} at {path}, got {gotStatus} {gotCode} at {gotPath}");
        }

        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.Equal(0, await core.Items.CountAsync(i => i.Kind == "property"));
    }

    [Fact]
    public async Task Editing_changes_only_what_is_sent_and_a_stale_version_is_a_conflict()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        var first = await VersionAsync(app, id);
        Task<HttpResponseMessage> PatchAsync(object body) => app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Properties}/{id}", body);

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(new
        {
            version = first, type = "rental", purchasePrice = "255000", locationLabel = "Nantes", rentalStartsOn = "2021-01-01",
        })).StatusCode);
        var detail = await PropertyAsync(app, id);
        Assert.Equal(("rental", "255000.00", "19000.00", "Nantes", "2021-01-01"),
            (S(detail, "type"), S(detail, "purchasePrice"), S(detail, "notaryFees"), S(detail, "locationLabel"), S(detail, "rentalStartsOn")));
        var second = detail.GetProperty("version").GetUInt32();
        Assert.NotEqual(first, second);

        // A rename is the Core item's (precision 4): the version does not move, the next PATCH read before it goes through.
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(new { version = second, name = "Appartement" })).StatusCode);
        Assert.Equal(second, await VersionAsync(app, id));
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(new
        {
            version = second, clearLocationLabel = true, clearRentalStartsOn = true, staleAfterDays = 200,
        })).StatusCode);
        detail = await PropertyAsync(app, id);
        Assert.Equal(("Appartement", (string?)null, (string?)null, 200),
            (S(detail, "name"), S(detail, "locationLabel"), S(detail, "rentalStartsOn"), detail.GetProperty("staleAfterDays").GetInt32()));

        Assert.Equal((HttpStatusCode.Conflict, "realestate.property_conflict", (string?)null),
            await RefusalAsync(await PatchAsync(new { version = first, note = "trop tard" })));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "request.required", "version"),
            await RefusalAsync(await PatchAsync(new { note = "sans version" })));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_property_field", "valuationCadence"),
            await RefusalAsync(await PatchAsync(new { version = second, valuationCadence = "monthly" })));
        Assert.Equal(JsonValueKind.Null, (await PropertyAsync(app, id)).GetProperty("note").ValueKind);
    }

    [Fact]
    public async Task A_purchase_moved_after_the_rental_start_answers_at_purchaseDate() // Review Focus #1
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Home(app);
        body["rentalStartsOn"] = "2021-01-01";
        var id = await NewPropertyAsync(app, body);
        var version = await VersionAsync(app, id);

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_property_field", "purchaseDate"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Properties}/{id}", new { version, purchaseDate = "2021-06-01" })));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_property_field", "rentalStartsOn"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Properties}/{id}", new { version, rentalStartsOn = "2020-01-01" })));

        var detail = await PropertyAsync(app, id);
        Assert.Equal(("2020-06-15", "2021-01-01", version),
            (S(detail, "purchaseDate"), S(detail, "rentalStartsOn"), detail.GetProperty("version").GetUInt32()));
    }

    [Fact]
    public async Task Another_adults_private_property_a_loan_and_an_envelope_are_not_found_on_any_route()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await NewPropertyAsync(app, Home(app, name: "Maison de B", visibility: "private", owners: [app.AdultB]), app.AdultB);
        var loan = await NewAsync(app, Golden(app));
        var envelope = (await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared)).Value;

        foreach (var target in new[] { hidden, loan, envelope })
        foreach (var (method, url, body) in Routes(target))
        {
            var response = await app.RawAsync(app.AdultA, method, url, body);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {url}: {response.StatusCode}");
        }

        Assert.Empty((await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Properties}?scope=me"))).GetProperty("items").EnumerateArray());
    }

    private static IEnumerable<(HttpMethod Method, string Url, object? Body)> Routes(Guid id)
    {
        var other = Guid.NewGuid();
        var flow = new { occurredOn = "2025-06-01", kind = "expense", amount = "100", category = "works" };
        return
        [
            (HttpMethod.Get, $"{Properties}/{id}", null),
            (HttpMethod.Patch, $"{Properties}/{id}", new { version = 1, note = "x" }),
            (HttpMethod.Post, $"{Properties}/{id}/valuations", new { asOf = "2025-06-01", amount = "290000" }),
            (HttpMethod.Delete, $"{Properties}/{id}/valuations/{other}", null),
            (HttpMethod.Post, $"{Properties}/{id}/flows", flow),
            (HttpMethod.Put, $"{Properties}/{id}/flows/{other}", flow),
            (HttpMethod.Delete, $"{Properties}/{id}/flows/{other}", null),
        ];
    }
}
