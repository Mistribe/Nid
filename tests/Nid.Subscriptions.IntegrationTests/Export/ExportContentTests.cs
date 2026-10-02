using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Subscriptions.IntegrationTests.Import;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Export;

[Collection("postgres")]
public class ExportContentTests(PostgresFixture fixture)
{
    private static object Adults(NidApp app) => new Dictionary<string, Guid> { ["adult-1"] = app.AdultA.Value, ["adult-2"] = app.AdultB.Value };

    private static async Task CommitAsync(NidApp app, Core.Contracts.PersonId actor, IReadOnlyDictionary<string, string> sections)
    {
        var client = new ImportClient(app);
        var validated = await client.RunAsync(actor, Adults(app), sections);
        Assert.True(validated.State == "validated", string.Join("; ", validated.Report!.Issues.Select(i => $"{i.Code} at {i.Path}: {i.Message}")));
        Assert.True((await client.CommitRawAsync(actor, validated.Id)).IsSuccessStatusCode);
    }

    private static string Text(JsonElement e, string name) => e.GetProperty(name).ToString();

    [Fact]
    public async Task The_golden_file_comes_back_with_its_ids_references_and_amounts_as_strings()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await CommitAsync(app, app.AdultA, GoldenFile.Sections());

        var export = await new ExportClient(app).ExportAllAsync(app.AdultA);

        Assert.Equal(["sub-nordvpn", "sub-spotify", "sub-wow"], export["subscriptions"].Select(s => Text(s, "id")).Order());
        var spotify = export["subscriptions"].Single(s => Text(s, "id") == "sub-spotify");
        Assert.Equal("spotify", Text(spotify, "provider_key"));
        Assert.Equal("shared", Text(spotify, "visibility"));
        var owner = Text(spotify.GetProperty("ownership")[0], "person");
        Assert.StartsWith("adult-", owner);
        Assert.Equal(["ph-spotify-1", "ph-spotify-2"], export["billing_phases"].Select(p => Text(p, "id")));
        // Written as a string. The golden file gives this amount as a JSON number, which the import reads as 17.2.
        var amount = export["billing_phases"][1].GetProperty("price").GetProperty("amount");
        Assert.Equal(JsonValueKind.String, amount.ValueKind);
        Assert.Equal(17.20m, decimal.Parse(amount.GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("15.17", export["billing_phases"][0].GetProperty("price").GetProperty("amount").GetString());
        Assert.Equal(JsonValueKind.Object, export["billing_phases"][1].GetProperty("covers").ValueKind);
        Assert.Equal(["nordvpn", "spotify", "wow"], export["providers"].Select(p => Text(p, "key")).Order());
        Assert.Equal(owner, Text(Assert.Single(export["persons"]), "ref"));
        var bundle = export["purchases"].Single(p => Text(p, "id") == "pu-wow");
        Assert.Equal("0", bundle.GetProperty("allocated_amount").GetString());
        Assert.Equal("sub-wow", Text(bundle.GetProperty("grants")[0], "subscription_id"));
    }

    [Fact]
    public async Task Nothing_of_the_other_adults_private_items_reaches_the_export()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await CommitAsync(app, app.AdultA, new Dictionary<string, string>
        {
            ["persons"] = """[{"ref":"adult-1","kind":"adult","display_name":"A"},{"ref":"adult-2","kind":"adult","display_name":"B"},{"ref":"kid","kind":"dependent","display_name":"Secret kid"}]""",
            ["providers"] = """[{"key":"secret-tool","name":"Secret tool","label_keys":["other"]}]""",
            ["subscriptions"] = """
                [{"id":"sub-shared","name":"Shared","ownership":[{"person":"adult-1","share_bp":5000},{"person":"adult-2","share_bp":5000}],"auto_renew":true},
                 {"id":"sub-private","name":"Private of A","visibility":"private","provider_key":"secret-tool","ownership":[{"person":"adult-1","share_bp":10000}],"service_users":["kid"],"auto_renew":true}]
                """,
            ["purchases"] = """
                [{"id":"pu-both","date":"2025-01-01","label":"Bundle","kind":"bundle","price":{"amount":"30","currency":"EUR"},"payer":{"type":"household"},"allocated_amount":"20",
                  "grants":[{"subscription_id":"sub-shared","duration":{"count":1,"unit":"month"},"source":"included"},{"subscription_id":"sub-private","duration":{"count":1,"unit":"month"},"source":"included"}]}]
                """,
        });

        var export = await new ExportClient(app).ExportAllAsync(app.AdultB);

        Assert.Equal(["sub-shared"], export["subscriptions"].Select(s => Text(s, "id")));
        Assert.Empty(export["providers"]);
        Assert.DoesNotContain(export["persons"], p => Text(p, "display_name") == "Secret kid");
        var purchase = Assert.Single(export["purchases"]);
        Assert.Equal(["sub-shared"], purchase.GetProperty("grants").EnumerateArray().Select(g => Text(g, "subscription_id")));
        Assert.DoesNotContain("sub-private", JsonSerializer.Serialize(export));
        Assert.DoesNotContain("Secret", JsonSerializer.Serialize(export));
    }

    [Fact]
    public async Task Two_rows_imported_under_one_ref_get_distinct_ids()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = """[{"id":"sub-p","name":"B's","ownership":[{"person":"adult-2","share_bp":10000}],"visibility":"private","auto_renew":true}]""";
        await CommitAsync(app, app.AdultA, new Dictionary<string, string> { ["persons"] = GoldenFile.Persons, ["subscriptions"] = """[{"id":"sub-p","name":"A's","visibility":"private","ownership":[{"person":"adult-1","share_bp":10000}],"auto_renew":true}]""" });
        await CommitAsync(app, app.AdultB, new Dictionary<string, string> { ["persons"] = GoldenFile.Persons, ["subscriptions"] = mine });
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
        {
            var item = await core.Items.SingleAsync(i => i.DisplayName == "A's");
            item.Visibility = "shared";
            await core.SaveChangesAsync();
        }

        var export = await new ExportClient(app).ExportAllAsync(app.AdultB);

        var ids = export["subscriptions"].ToDictionary(s => Text(s, "name"), s => Text(s, "id"));
        Assert.Equal("sub-p", ids["B's"]);
        Assert.StartsWith("sub-", ids["A's"]);
        Assert.NotEqual("sub-p", ids["A's"]);
    }

    [Fact]
    public async Task Envelopes_are_exported_as_file_records_closed_ones_included()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await CommitAsync(app, app.AdultA, EnvelopesImportTests.File());
        var made = await app.NewEnvelopeAsync("Zeta", app.AdultA, Core.Contracts.Visibility.Shared);

        var export = await new ExportClient(app).ExportAllAsync(app.AdultA);

        var envelopes = export["envelopes"];
        Assert.Equal(["Livret A", "PEA", "Zeta"], envelopes.Select(e => Text(e, "name")));
        Assert.Equal(["env-livret", "env-pea", $"env-{made.Value:D}"], envelopes.Select(e => Text(e, "id")));
        var livret = envelopes[0];
        Assert.Equal("savings", Text(livret, "kind"));
        Assert.Equal("EUR", Text(livret, "currency"));
        Assert.Equal("monthly", Text(livret, "valuation_cadence"));
        Assert.Equal("shared", Text(livret, "visibility"));
        Assert.Equal("2025-07-31", Text(livret, "closed_on"));
        Assert.Equal(["adult-1", "adult-2"], livret.GetProperty("ownership").EnumerateArray().Select(o => Text(o, "person")).Order());
        Assert.Equal([5000, 5000], livret.GetProperty("ownership").EnumerateArray().Select(o => o.GetProperty("share_bp").GetInt32()));
        var pea = envelopes[1];
        Assert.Equal("private", Text(pea, "visibility"));
        Assert.Equal("Banque", Text(pea, "institution"));
        Assert.Equal("1234", Text(pea, "account_hint"));
        Assert.Equal("2019-03-01", Text(pea, "opened_on"));
        Assert.Equal(JsonValueKind.Null, pea.GetProperty("closed_on").ValueKind);
        Assert.Equal(2, export["persons"].Count);

        var ofB = await new ExportClient(app).ExportAllAsync(app.AdultB);

        Assert.Equal(["Livret A", "Zeta"], ofB["envelopes"].Select(e => Text(e, "name")));
        Assert.DoesNotContain("env-pea", JsonSerializer.Serialize(ofB));
        Assert.DoesNotContain("Banque", JsonSerializer.Serialize(ofB));
    }

    [Fact]
    public async Task Values_and_flows_are_exported_by_item_date_and_id_with_amounts_as_strings()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await CommitAsync(app, app.AdultA, FactsImportTests.Facts());

        var export = await new ExportClient(app).ExportAllAsync(app.AdultA);

        var values = export["valuations"];
        Assert.Equal(["v-livret-0", "v-pea-1", "v-pea-2"], values.Select(v => Text(v, "id")));
        Assert.Equal(["env-livret", "env-pea", "env-pea"], values.Select(v => Text(v, "item")));
        Assert.Equal(["2025-07-31", "2024-12-31", "2025-09-30"], values.Select(v => Text(v, "as_of")));
        Assert.All(values, v => Assert.Equal(JsonValueKind.String, v.GetProperty("amount").GetProperty("amount").ValueKind));
        Assert.Equal([0m, 21000m, 25000m], values.Select(v => Amount(v)));
        Assert.All(values, v => Assert.Equal("EUR", Text(v.GetProperty("amount"), "currency")));
        Assert.Equal("clôture", Text(values[0], "note"));
        Assert.Equal(JsonValueKind.Null, values[1].GetProperty("note").ValueKind);

        var flows = export["flows"];
        Assert.Equal(["f-tr-in", "f-livret-out", "f-pea-open", "f-pea-in", "f-pea-fee", "f-tr-out"], flows.Select(f => Text(f, "id")));
        Assert.Equal(["contribution", "withdrawal", "opening", "contribution", "expense", "withdrawal"], flows.Select(f => Text(f, "kind")));
        Assert.Equal([500m, 500m, 20000m, 2000m, 0.1m, 500m], flows.Select(f => Amount(f)));
        var leg = flows[5];
        Assert.Equal(("env-pea", "2025-07-01", "transfer", "transfer:t1"), (Text(leg, "item"), Text(leg, "occurred_on"), Text(leg, "category"), Text(leg, "external_ref")));
        Assert.Equal(JsonValueKind.Null, leg.GetProperty("note").ValueKind);
        Assert.Equal("fees", Text(flows[4], "category"));
        Assert.Equal("clôture", Text(flows[1], "note"));
        var opening = flows[2];
        Assert.Equal(JsonValueKind.Null, opening.GetProperty("category").ValueKind);
        Assert.Equal(JsonValueKind.Null, opening.GetProperty("external_ref").ValueKind);

        var ofB = await new ExportClient(app).ExportAllAsync(app.AdultB);

        Assert.Equal(["v-livret-0"], ofB["valuations"].Select(v => Text(v, "id")));
        Assert.Equal(["f-tr-in", "f-livret-out"], ofB["flows"].Select(f => Text(f, "id")));
        Assert.DoesNotContain("env-pea", JsonSerializer.Serialize(ofB));
    }

    [Fact]
    public async Task Income_sources_and_phases_are_exported_as_file_records_ended_ones_included()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await CommitAsync(app, app.AdultA, IncomeImportTests.IncomeFile());
        var zeta = await app.NewIncomeSourceAsync("Zeta", Core.Contracts.Visibility.Shared, (app.AdultA, 10_000));
        await app.NewIncomeSourceAsync("Secret de B", Core.Contracts.Visibility.Private, (app.AdultB, 10_000));

        var export = await new ExportClient(app).ExportAllAsync(app.AdultA);

        var sources = export["income_sources"];
        Assert.Equal(["CDD", "Salaire", "Zeta"], sources.Select(s => Text(s, "name")));
        Assert.Equal(["inc-cdd", "inc-salary-1", $"inc-{zeta.Value:D}"], sources.Select(s => Text(s, "id")));
        var cdd = sources[0];
        Assert.Equal(("salary", "EUR", "shared", "2025-06-30"), (Text(cdd, "kind"), Text(cdd, "currency"), Text(cdd, "visibility"), Text(cdd, "ended_on")));
        Assert.Equal(["adult-1", "adult-2"], cdd.GetProperty("ownership").EnumerateArray().Select(o => Text(o, "person")));
        Assert.Equal(JsonValueKind.Null, cdd.GetProperty("payer_label").ValueKind);
        var salary = sources[1];
        Assert.Equal("Employeur", Text(salary, "payer_label"));
        Assert.Equal(JsonValueKind.Null, salary.GetProperty("ended_on").ValueKind);

        var phases = export["income_phases"];
        var expected = new List<(string Source, string Label, string StartsOn, string Id)>
        {
            ("inc-cdd", "Salaire de base", "2025-01-01", "ip-cdd"),
            ("inc-salary-1", "Prime annuelle", "2024-12-15", "ip-3"),
            ("inc-salary-1", "Salaire de base", "2024-09-01", "ip-1"),
            ("inc-salary-1", "Salaire de base", "2025-09-01", "ip-2"),
        };
        var zetaPhase = phases.Single(p => Text(p, "source") == $"inc-{zeta.Value:D}");
        Assert.StartsWith("ip-", Text(zetaPhase, "id"));
        expected.Add(($"inc-{zeta.Value:D}", "Salaire", "2025-01-01", Text(zetaPhase, "id")));
        Assert.Equal(
            expected.OrderBy(p => p.Source, StringComparer.Ordinal).ThenBy(p => p.Label, StringComparer.Ordinal).ThenBy(p => p.StartsOn, StringComparer.Ordinal),
            phases.Select(p => (Text(p, "source"), Text(p, "label"), Text(p, "starts_on"), Text(p, "id"))));
        var bonus = phases.Single(p => Text(p, "id") == "ip-3");
        Assert.Equal((1, "year"), (bonus.GetProperty("every").GetProperty("count").GetInt32(), Text(bonus.GetProperty("every"), "unit")));
        Assert.Equal(JsonValueKind.String, bonus.GetProperty("net_amount").GetProperty("amount").ValueKind);
        Assert.Equal(1800m, Amount(bonus.GetProperty("net_amount").GetProperty("amount").GetString()!));
        Assert.Equal(JsonValueKind.Null, bonus.GetProperty("ends_on").ValueKind);
        Assert.Equal("2025-09-01", Text(phases.Single(p => Text(p, "id") == "ip-1"), "ends_on"));
        var oneOff = Assert.Single(export["flows"]);
        Assert.Equal(("f-bonus", "inc-salary-1", "income", "one_off"), (Text(oneOff, "id"), Text(oneOff, "item"), Text(oneOff, "kind"), Text(oneOff, "category")));

        // The other adult's private source is nowhere in adult-1's export.
        Assert.DoesNotContain("Secret de B", JsonSerializer.Serialize(export));
        var ofB = await new ExportClient(app).ExportAllAsync(app.AdultB);
        Assert.Equal(["CDD", "Salaire", "Secret de B", "Zeta"], ofB["income_sources"].Select(s => Text(s, "name")));
    }

    private static decimal Amount(string amount) => decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);

    private static decimal Amount(JsonElement fact)
        => decimal.Parse(fact.GetProperty("amount").GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
}
