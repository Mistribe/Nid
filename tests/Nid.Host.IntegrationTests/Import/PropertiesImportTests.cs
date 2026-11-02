using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.Host.IntegrationTests.Export;
using Nid.TestSupport;
using static Nid.Host.IntegrationTests.Import.EnvelopesImportTests;
using static Nid.Host.IntegrationTests.Import.EstateFile;

namespace Nid.Host.IntegrationTests.Import;

/// <summary>Spec 3F1 §2–§4: the <c>properties</c>, <c>property_recurring_costs</c> and <c>tenancies</c> sections, through the real property API.</summary>
public class PropertiesImportTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    internal static async Task<List<JsonElement>> PropertiesAsync(NidApp app, PersonId reader)
        => (await JsonAsync(app, reader, "/api/v1/realestate/properties?scope=me")).GetProperty("items").EnumerateArray().ToList();

    internal static async Task<Guid> PropertyIdAsync(NidApp app, string name, PersonId? reader = null)
        => (await PropertiesAsync(app, reader ?? app.AdultA)).Single(p => S(p, "name") == name).GetProperty("id").GetGuid();

    internal static async Task<JsonElement> PropertyAsync(NidApp app, string name, PersonId? reader = null)
        => await JsonAsync(app, reader ?? app.AdultA, $"/api/v1/realestate/properties/{await PropertyIdAsync(app, name, reader)}");

    internal static List<(string Code, string Path)> ErrorsOf(SessionResponse session)
        => session.Report!.Issues.Where(i => i.Severity == "error").Select(i => (i.Code, i.Path)).ToList();

    internal static string S(JsonElement element, string name) => element.GetProperty(name).GetString()!;

    [Fact]
    public async Task Properties_their_planned_costs_and_tenancies_are_created()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var committed = await CommitAsync(new ImportClient(app), app, app.AdultA, Properties());

        Assert.Equal(new CountsResponse(2, 0, 0), committed.Report!.Sections["properties"]);
        Assert.Equal(new CountsResponse(2, 0, 0), committed.Report.Sections["property_recurring_costs"]);
        Assert.Equal(new CountsResponse(2, 0, 0), committed.Report.Sections["tenancies"]);
        var home = await PropertyAsync(app, "Maison");
        Assert.Equal(("primary_residence", "EUR", "shared", "2020-06-15"), (S(home, "type"), S(home, "currency"), S(home, "visibility"), S(home, "purchaseDate")));
        Assert.Equal(("250000.00", "19000.00", "0.00", "yearly"),
            (S(home, "purchasePrice"), S(home, "notaryFees"), S(home, "agencyFees"), S(home, "valuationCadence")));
        Assert.Equal(2, home.GetProperty("owners").GetArrayLength());
        var tax = Assert.Single(home.GetProperty("recurringCosts").EnumerateArray());
        Assert.Equal(("property_tax", "1200.00", "year", "2020-10-15"), (S(tax, "category"), S(tax, "amount"), S(tax.GetProperty("every"), "unit"), S(tax, "startsOn")));
        var flat = await PropertyAsync(app, "Studio");
        Assert.Equal(("rental", "24.50", "Lyon 7e", "2021-04-01"), (S(flat, "type"), S(flat, "surfaceM2"), S(flat, "locationLabel"), S(flat, "rentalStartsOn")));
        Assert.Equal("syndic", S(flat.GetProperty("recurringCosts")[0], "note"));
        Assert.Equal([("2021-04-01", "650.00", "650.00"), ("2024-04-01", "700.00", null)],
            flat.GetProperty("tenancies").EnumerateArray().Select(t => (S(t, "startsOn"), S(t, "rentMonthly"), t.GetProperty("deposit").GetString())));
        // Both are shared: the co-owner reads the home, and the studio, adult-1's alone.
        Assert.Equal(["Maison", "Studio"], (await PropertiesAsync(app, app.AdultB)).Select(p => S(p, "name")).Order());
        await using var estate = PostgresFixture.NewRealEstateContext(app.ConnectionString);
        Assert.Equal(["prop-flat", "prop-home"], (await estate.Properties.Select(p => p.ImportRef!).ToListAsync(cancellationToken: TestContext.Current.CancellationToken)).Order());
        Assert.All(await estate.Tenancies.ToListAsync(cancellationToken: TestContext.Current.CancellationToken), t => Assert.Equal(app.AdultA.Value, t.ImportedBy));
    }

    [Fact]
    public async Task The_same_file_again_is_unchanged()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, Properties());

        var again = await CommitAsync(client, app, app.AdultA, Properties());

        Assert.Equal(new CountsResponse(0, 2, 0), again.Report!.Sections["properties"]);
        Assert.Equal(new CountsResponse(0, 2, 0), again.Report.Sections["property_recurring_costs"]);
        Assert.Equal(new CountsResponse(0, 2, 0), again.Report.Sections["tenancies"]);
        Assert.Empty(again.Report.Issues);
        Assert.Equal(2, (await PropertiesAsync(app, app.AdultA)).Count);
        Assert.Equal(2, (await PropertyAsync(app, "Studio")).GetProperty("tenancies").GetArrayLength());
    }

    [Fact]
    public async Task Other_content_is_a_conflict_and_replace_updates_in_place()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, Properties());
        var before = (await PropertyAsync(app, "Studio")).GetProperty("tenancies")[1].GetProperty("id").GetGuid();
        var raised = Properties(tenancies: List(Lease1, Lease2.Replace("700.00", "720.00")));

        var validated = await ValidateAsync(client, app, app.AdultA, raised);

        Assert.Equal("invalid", validated.State);
        Assert.Equal([(ImportCodes.Conflict, "tenancies[1]")], ErrorsOf(validated));
        Assert.Equal(new CountsResponse(0, 1, 1), validated.Report!.Sections["tenancies"]);
        await client.CancelRawAsync(app.AdultA, validated.Id);

        var replaced = await CommitAsync(client, app, app.AdultA, raised, replace: true);

        Assert.Equal(new CountsResponse(0, 1, 0, 1), replaced.Report!.Sections["tenancies"]);
        Assert.Equal(new CountsResponse(0, 2, 0), replaced.Report.Sections["properties"]);
        var lease = (await PropertyAsync(app, "Studio")).GetProperty("tenancies")[1];
        Assert.Equal((before, "720.00"), (lease.GetProperty("id").GetGuid(), S(lease, "rentMonthly")));

        // A tenancy stays on its property, whatever the mode.
        var moved = await ValidateAsync(client, app, app.AdultA,
            Properties(tenancies: List(Lease1, Lease2.Replace("700.00", "730.00").Replace("\"property\":\"prop-flat\"", "\"property\":\"prop-home\""))), replace: true);

        Assert.Equal([(ImportCodes.InvalidValue, "tenancies[1].property")], ErrorsOf(moved));
    }

    /// <summary>Review Focus 1: an edit made in the app is the household's; the same file leaves it, a changed file conflicts, replace brings the file back.</summary>
    [Fact]
    public async Task A_tenancy_edited_in_the_app_reimports_unchanged_and_replace_brings_the_file_back()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, Properties());
        var flat = await PropertyIdAsync(app, "Studio");
        var lease = (await PropertyAsync(app, "Studio")).GetProperty("tenancies")[1];
        var edited = await app.RawAsync(app.AdultA, HttpMethod.Put, $"/api/v1/realestate/properties/{flat}/tenancies/{lease.GetProperty("id").GetGuid()}", new
        {
            label = "T1 meublé", startsOn = "2024-04-01", rentMonthly = "710", chargesMonthly = "50", version = lease.GetProperty("version").GetUInt32(),
        });
        Assert.True(edited.IsSuccessStatusCode, await edited.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var same = await CommitAsync(client, app, app.AdultA, Properties());

        Assert.Equal(new CountsResponse(0, 2, 0), same.Report!.Sections["tenancies"]);
        Assert.Equal("710.00", S((await PropertyAsync(app, "Studio")).GetProperty("tenancies")[1], "rentMonthly"));

        var changed = Properties(tenancies: List(Lease1, Lease2.Replace("700.00", "720.00")));
        var conflict = await ValidateAsync(client, app, app.AdultA, changed);
        Assert.Equal([(ImportCodes.Conflict, "tenancies[1]")], ErrorsOf(conflict));
        await client.CancelRawAsync(app.AdultA, conflict.Id);

        await CommitAsync(client, app, app.AdultA, changed, replace: true);

        Assert.Equal("720.00", S((await PropertyAsync(app, "Studio")).GetProperty("tenancies")[1], "rentMonthly"));
    }

    [Fact]
    public async Task Every_rule_is_reported_at_its_field()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);

        // Rules local to an entity: the receipt of its batch reports them.
        var session = await client.StartAsync(app.AdultA, Adults(app));
        var properties = await client.PutAsync(app.AdultA, session.Id, "properties", 0, List(
            Home.Replace("primary_residence", "castle"),
            Home.Replace("prop-home", "p1").Replace("\"amount\":\"250000.00\"", "\"amount\":\"0\""),
            Home.Replace("prop-home", "p2").Replace("\"amount\":\"19000.00\"", "\"amount\":\"-1\""),
            Flat.Replace("prop-flat", "p3").Replace("\"rental_starts_on\":\"2021-04-01\"", "\"rental_starts_on\":\"2021-02-01\""),
            Home.Replace("prop-home", "p4").Replace("\"amount\":\"250000.00\",\"currency\":\"EUR\"", "\"amount\":\"250000.00\",\"currency\":\"USD\"")));
        var costs = await client.PutAsync(app.AdultA, session.Id, "property_recurring_costs", 0, List(
            Tax.Replace("\"unit\":\"year\"", "\"unit\":\"week\""),
            Tax.Replace("cost-tax", "c1").Replace("property_tax", "works"),
            Tax.Replace("cost-tax", "c2").Replace("\"unit\":\"year\"", "\"unit\":\"fortnight\"")));
        var tenancies = await client.PutAsync(app.AdultA, session.Id, "tenancies", 0, List(
            Lease2.Replace("\"amount\":\"700.00\"", "\"amount\":\"0\""),
            Lease1.Replace("ten-1", "t1").Replace("\"ends_on\":\"2024-04-01\"", "\"ends_on\":\"2021-03-01\"")));
        await client.CancelRawAsync(app.AdultA, session.Id);

        Assert.False(properties.Accepted);
        Assert.Equal(
            [
                (ImportCodes.InvalidValue, "properties[0].property_type"),
                (PropertyCodes.InvalidPropertyField, "properties[1].purchase_price.amount"),
                (PropertyCodes.InvalidPropertyField, "properties[2].notary_fees.amount"),
                (PropertyCodes.InvalidPropertyField, "properties[3].rental_starts_on"),
                (CoreFactCodes.CurrencyMismatch, "properties[4].purchase_price.currency"),
            ],
            properties.Issues.Select(i => (i.Code, i.Path)));
        Assert.Equal(
            [
                (PropertyCodes.InvalidPropertyField, "property_recurring_costs[0].every.unit"),
                (PropertyCodes.InvalidCategory, "property_recurring_costs[1].category"),
                (ImportCodes.InvalidValue, "property_recurring_costs[2].every.unit"),
            ],
            costs.Issues.Select(i => (i.Code, i.Path)));
        Assert.Equal(
            [
                (PropertyCodes.InvalidPropertyField, "tenancies[0].rent_monthly.amount"),
                (PropertyCodes.InvalidPropertyField, "tenancies[1].ends_on"),
            ],
            tenancies.Issues.Select(i => (i.Code, i.Path)));

        // Rules that need the household's today or the other entities: the validation reports them.
        var validated = await ValidateAsync(client, app, app.AdultA, Properties(
            List(Home.Replace("2020-06-15", "2026-01-01"), Flat),
            List(Condo.Replace("\"currency\":\"EUR\"", "\"currency\":\"USD\""), Tax.Replace("cost-tax", "cost-x").Replace("prop-home", "prop-nobody")),
            List(Lease1, Lease2.Replace("\"starts_on\":\"2024-04-01\"", "\"starts_on\":\"2024-03-01\""),
                Lease1.Replace("ten-1", "ten-3").Replace("\"starts_on\":\"2021-04-01\",\"ends_on\":\"2024-04-01\"", "\"starts_on\":\"2021-01-01\",\"ends_on\":\"2021-02-01\""))));

        Assert.Equal("invalid", validated.State);
        Assert.Equal(
            new[]
            {
                (PropertyCodes.InvalidPropertyField, "properties[0].purchase_date"),
                (CoreFactCodes.CurrencyMismatch, "property_recurring_costs[0].amount.currency"),
                (ImportCodes.UnresolvedReference, "property_recurring_costs[1].property"),
                (PropertyCodes.TenancyOverlap, "tenancies[1].starts_on"),
                (PropertyCodes.InvalidPropertyField, "tenancies[2].starts_on"),
            }.Order(),
            ErrorsOf(validated).Order());
        await client.CancelRawAsync(app.AdultA, validated.Id);

        // An item is imported by one of its owners.
        var unowned = await ValidateAsync(client, app, app.AdultA, Properties(List(Home, Flat.Replace("\"person\":\"adult-1\"", "\"person\":\"adult-2\"")), "[]", "[]"));

        Assert.Equal([(ImportCodes.NotPermitted, "properties[1]")], ErrorsOf(unowned));
    }

    [Fact]
    public async Task Replace_follows_the_1F3b_rules()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, Properties());

        // A replacement keeps the currency.
        var usd = await ValidateAsync(client, app, app.AdultA, Properties(List(Home.Replace("\"EUR\"", "\"USD\""), Flat), "[]", "[]"), replace: true);
        Assert.Equal([(CoreFactCodes.CurrencyMismatch, "properties[0].currency")], ErrorsOf(usd));
        await client.CancelRawAsync(app.AdultA, usd.Id);

        // Adult-2 reads the studio but cannot change it, nor add a tenancy to it.
        var renamed = await ValidateAsync(client, app, app.AdultB, Properties(List(Home, Flat.Replace("Studio", "Studio 2")), "[]", "[]"), replace: true);
        Assert.Equal([(ImportCodes.NotPermitted, "properties[1]")], ErrorsOf(renamed));
        await client.CancelRawAsync(app.AdultB, renamed.Id);

        var added = await ValidateAsync(client, app, app.AdultB,
            Properties(tenancies: List(Lease1, Lease2, Lease2.Replace("ten-2", "ten-b").Replace("2024-04-01", "2030-01-01"))));
        Assert.Equal([(ImportCodes.NotPermitted, "tenancies[2]")], ErrorsOf(added));
    }

    /// <summary>Spec 3F1 §3 and decision 2: a property flow outside the module's convention refuses the file; an envelope's flow is not concerned.</summary>
    [Fact]
    public async Task A_property_flow_outside_the_convention_is_refused_and_an_envelope_flow_is_not()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        const string works = """{"id":"f-works","item":"prop-home","occurred_on":"2021-05-10","kind":"expense","amount":{"amount":"12000","currency":"EUR"},"category":"works"}""";
        const string envelope = """{"id":"f-env","item":"env-pea","occurred_on":"2025-03-15","kind":"expense","amount":{"amount":"2","currency":"EUR"},"category":"fees"}""";
        var file = Properties();
        file["envelopes"] = Envelopes;
        file["flows"] = List(works,
            """{"id":"f-fees","item":"prop-home","occurred_on":"2025-01-10","kind":"expense","amount":{"amount":"10","currency":"EUR"},"category":"fees"}""",
            """{"id":"f-in","item":"prop-home","occurred_on":"2025-01-10","kind":"contribution","amount":{"amount":"10","currency":"EUR"}}""",
            """{"id":"f-none","item":"prop-flat","occurred_on":"2025-01-10","kind":"income","amount":{"amount":"700","currency":"EUR"}}""",
            envelope);

        var refused = await ValidateAsync(client, app, app.AdultA, file);

        Assert.Equal(
            [(PropertyCodes.InvalidCategory, "flows[1].category"), (PropertyCodes.InvalidCategory, "flows[2].kind"), (PropertyCodes.InvalidCategory, "flows[3].category")],
            ErrorsOf(refused));
        await client.CancelRawAsync(app.AdultA, refused.Id);

        file["flows"] = List(works, envelope);
        var committed = await CommitAsync(client, app, app.AdultA, file);

        Assert.Equal(new CountsResponse(2, 0, 0), committed.Report!.Sections["flows"]);
        var flow = Assert.Single((await PropertyAsync(app, "Maison")).GetProperty("flows").EnumerateArray());
        Assert.Equal(("expense", "works", "12000.00"), (S(flow, "kind"), S(flow, "category"), S(flow, "amount")));
    }

    [Fact]
    public async Task An_id_of_another_adults_private_property_answers_as_a_new_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var secret = Flat.Replace("Studio", "Secret B").Replace("\"person\":\"adult-1\"", "\"person\":\"adult-2\"")
            .Replace("\"share_bp\":10000}]", "\"share_bp\":10000}],\"visibility\":\"private\"");
        await CommitAsync(client, app, app.AdultB, Properties(List(secret), "[]", List(Lease1)));

        var fresh = await ValidateAsync(client, app, app.AdultA,
            Properties(List(Flat.Replace("prop-flat", "prop-fresh")), "[]", List(Lease1.Replace("ten-1", "ten-fresh").Replace("prop-flat", "prop-fresh"))));
        await client.CancelRawAsync(app.AdultA, fresh.Id);
        var taken = await ValidateAsync(client, app, app.AdultA, Properties(List(Flat), "[]", List(Lease1)));

        Assert.Equal("validated", taken.State);
        Assert.Equal(new CountsResponse(1, 0, 0), taken.Report!.Sections["properties"]);
        Assert.Equal(new CountsResponse(1, 0, 0), taken.Report.Sections["tenancies"]);
        Assert.Equal(JsonSerializer.Serialize(fresh.Report), JsonSerializer.Serialize(taken.Report));
        var committed = await client.CommitRawAsync(app.AdultA, taken.Id);
        Assert.True(committed.IsSuccessStatusCode, await committed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["Studio"], (await PropertiesAsync(app, app.AdultA)).Select(p => S(p, "name")));
        // Adult-2's property and tenancy are as they were.
        Assert.Single((await PropertyAsync(app, "Secret B", app.AdultB)).GetProperty("tenancies").EnumerateArray());
    }

    [Fact]
    public async Task The_export_writes_the_properties_the_reader_reads_with_their_costs_and_tenancies()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await CommitAsync(new ImportClient(app), app, app.AdultA, Properties());
        await PropertyApi.NewPropertyAsync(app, PropertyApi.Home(app, "Hidden house", "private", app.AdultB), app.AdultB);

        var export = await new ExportClient(app).ExportAllAsync(app.AdultA);

        Assert.Equal(["prop-home", "prop-flat"], export["properties"].Select(p => S(p, "id"))); // by name: Maison, Studio
        var home = export["properties"][0];
        Assert.Equal(("Maison", "primary_residence", "EUR", "2020-06-15"), (S(home, "name"), S(home, "property_type"), S(home, "currency"), S(home, "purchase_date")));
        Assert.Equal(("250000.00", "19000.00", "0.00", "0.00"),
            (S(home.GetProperty("purchase_price"), "amount"), S(home.GetProperty("notary_fees"), "amount"),
                S(home.GetProperty("agency_fees"), "amount"), S(home.GetProperty("other_acquisition_costs"), "amount")));
        Assert.Equal(("yearly", "shared"), (S(home, "valuation_cadence"), S(home, "visibility")));
        Assert.Equal(["adult-1", "adult-2"], home.GetProperty("ownership").EnumerateArray().Select(o => S(o, "person")));
        Assert.Equal("24.50", S(export["properties"][1], "surface_m2"));
        Assert.Equal(["cost-condo", "cost-tax"], export["property_recurring_costs"].Select(c => S(c, "id"))); // by property file id
        var condo = export["property_recurring_costs"][0];
        Assert.Equal(("prop-flat", 3, "month"), (S(condo, "property"), condo.GetProperty("every").GetProperty("count").GetInt32(), S(condo.GetProperty("every"), "unit")));
        Assert.Equal(["ten-1", "ten-2"], export["tenancies"].Select(t => S(t, "id")));
        Assert.Equal(("50.00", JsonValueKind.Null), (S(export["tenancies"][1].GetProperty("charges_monthly"), "amount"), export["tenancies"][1].GetProperty("deposit").ValueKind));
        Assert.DoesNotContain("Hidden house", JsonSerializer.Serialize(export));

        // Adult-2 exports their private property under a key derived from its id.
        var forB = await new ExportClient(app).ExportAllAsync(app.AdultB);
        Assert.Contains(forB["properties"], p => S(p, "name") == "Hidden house" && S(p, "id").StartsWith("prop-", StringComparison.Ordinal));
    }
}
