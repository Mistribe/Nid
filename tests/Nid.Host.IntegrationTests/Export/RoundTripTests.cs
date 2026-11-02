using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Application.Catalog;
using Nid.Host.IntegrationTests.Import;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Export;

/// <summary>The criterion of 1D2 (stage-1 overview): an export re-imported into a fresh instance exports the same again.</summary>
public class RoundTripTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    /// <summary>
    /// Same catalog and same adult names in both instances, since the file carries the adults' names. The seeded
    /// dependent, which no file references, has a different name in each: person refs must not depend on who else
    /// lives in the household (§5.4).
    /// </summary>
    internal static async Task PrepareAsync(NidApp app, string unreferencedDependent)
    {
        app.Catalog.Serve(CatalogFixtures.RealLabels(), CatalogFixtures.RealProviders());
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System; // the catalog job runs as the system (1F3a U4)
            Assert.True((await scope.ServiceProvider.GetRequiredService<CatalogSync>().RunAsync(default)).TryGetValue(out _));
        }
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        (await core.Persons.SingleAsync(p => p.Id == app.AdultA.Value)).DisplayName = "Adulte 1";
        (await core.Persons.SingleAsync(p => p.Id == app.AdultB.Value)).DisplayName = "Adulte 2";
        (await core.Persons.SingleAsync(p => p.Id == app.Dependent.Value)).DisplayName = unreferencedDependent;
        await core.SaveChangesAsync();
    }

    private static object Adults(NidApp app) => new Dictionary<string, Guid> { ["adult-1"] = app.AdultA.Value, ["adult-2"] = app.AdultB.Value };

    internal static async Task CommitAsync(NidApp app, PersonId actor, IReadOnlyDictionary<string, string> sections)
    {
        var client = new ImportClient(app);
        var validated = await client.RunAsync(actor, Adults(app), sections);
        Assert.True(validated.State == "validated", string.Join("; ", validated.Report!.Issues.Select(i => $"{i.Code} at {i.Path}: {i.Message}")));
        var committed = await client.CommitRawAsync(actor, validated.Id);
        Assert.True(committed.IsSuccessStatusCode, await committed.Content.ReadAsStringAsync());
    }

    internal static Dictionary<string, string> AsFile(Dictionary<string, List<JsonElement>> export)
        => export.Where(s => s.Value.Count > 0).ToDictionary(s => s.Key, s => JsonSerializer.Serialize(s.Value));

    /// <summary>
    /// Spec §7: the reads agree. What differs by construction is left out: internal ids, provider keys (an import
    /// generates them), and a finding's reference (an internal id).
    /// </summary>
    private static async Task<string> ReadsAsync(NidApp app, PersonId reader)
    {
        var response = await app.RawAsync(reader, HttpMethod.Get, "/api/v1/subscriptions?scope=me&from=2024-01-01&to=2026-12-31&horizon=2028-01-01");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var list = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Strip(list);
        await NamePersonsAsync(app, list);
        var items = list["items"]!.AsArray().Select(i => i!.DeepClone()).OrderBy(i => i["displayName"]!.GetValue<string>(), StringComparer.Ordinal).ToList();
        list["items"] = new JsonArray(items.ToArray());
        return list.ToJsonString();

        static void Strip(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                foreach (var name in new[] { "id", "provider", "sharedPurchaseIds", "reference" })
                    obj.Remove(name);
                foreach (var (_, child) in obj.ToList())
                    Strip(child);
            }
            else if (node is JsonArray array)
                foreach (var child in array)
                    Strip(child);
        }
    }

    private static string WithoutLivretClosing(string entities, string id)
    {
        var kept = JsonNode.Parse(entities)!.AsArray().Where(e => e!["id"]!.GetValue<string>() != id).Select(e => e!.DeepClone()).ToArray();
        return new JsonArray(kept).ToJsonString();
    }

    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static async Task<JsonNode> SendAsync(NidApp app, HttpMethod method, string url, object? body = null)
    {
        var response = await app.RawAsync(app.AdultA, method, url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{method} {url}: {text}");
        return JsonNode.Parse(text) ?? new JsonObject();
    }

    private static async Task<JsonArray> EnvelopeListAsync(NidApp app)
        => (await SendAsync(app, HttpMethod.Get, "/api/v1/investments/envelopes?scope=me&archived=true"))["items"]!.AsArray();

    /// <summary>What the interface adds on top of an import: a correction, a transfer, a closing, and an envelope with no import reference.</summary>
    private static async Task AddEnvelopeDataThroughTheApiAsync(NidApp app)
    {
        var list = await EnvelopeListAsync(app);
        string IdOf(string name) => list.Single(e => e!["displayName"]!.GetValue<string>() == name)!["id"]!.GetValue<string>();
        var pea = IdOf("PEA");
        var livret = IdOf("Livret A");
        // Same date as an imported value: a correction, which supersedes it.
        await SendAsync(app, HttpMethod.Post, $"/api/v1/investments/envelopes/{pea}/valuations", new { asOf = "2025-09-30", amount = Eur("25500.00") });
        await SendAsync(app, HttpMethod.Post, "/api/v1/investments/transfers", new { from = pea, to = livret, occurredOn = "2025-08-01", amount = Eur("300.00") });
        await SendAsync(app, HttpMethod.Post, $"/api/v1/investments/envelopes/{livret}/close", new { closedOn = "2025-09-01", finalWithdrawal = Eur("800.00") });
        await SendAsync(app, HttpMethod.Post, "/api/v1/investments/envelopes", new
        {
            displayName = "Crypto", kind = "savings", currency = "EUR", visibility = "shared",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            firstValuation = new { asOf = "2025-06-30", amount = Eur("1234.00") },
        });
    }

    /// <summary>
    /// Issue #34 §5: a life insurance detailed by holdings, as the golden case of §8 one year earlier — a euro fund, a fund, two
    /// snapshots (the first recording the envelope's value), a switch and fees, the allocations tied to the contribution.
    /// </summary>
    private static async Task AddHoldingsThroughTheApiAsync(NidApp app)
    {
        var id = (await SendAsync(app, HttpMethod.Post, "/api/v1/investments/envelopes", new
        {
            displayName = "Assurance-vie", kind = "life_insurance", currency = "EUR", visibility = "shared",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
        }))["id"]!.GetValue<string>();
        var url = $"/api/v1/investments/envelopes/{id}";
        string Id(JsonNode node) => node["id"]!.GetValue<string>();
        var euro = Id(await SendAsync(app, HttpMethod.Post, $"{url}/holdings", new { name = "Fonds euros", kind = "euro_fund", assetClass = "capital_guaranteed" }));
        var fund = Id(await SendAsync(app, HttpMethod.Post, $"{url}/holdings", new
        {
            name = "Fonds A", kind = "fund", isin = "FR0000120271", issuer = "Gestion A", riskIndicator = 4, entryCostPct = "1.5", openedOn = "2023-01-01",
        }));
        var flow = Id(await SendAsync(app, HttpMethod.Post, $"{url}/flows", new { occurredOn = "2023-01-01", kind = "contribution", amount = Eur("10000.00") }));
        object Move(string on, string kind, string amount, string? units = null, string? price = null, string? flowId = null)
            => new { occurredOn = on, kind, amount = Eur(amount), units, unitPrice = price, envelopeFlowId = flowId };
        await SendAsync(app, HttpMethod.Post, $"{url}/holdings/{euro}/movements", Move("2023-01-01", "allocation_in", "3000.00", flowId: flow));
        await SendAsync(app, HttpMethod.Post, $"{url}/holdings/{fund}/movements", Move("2023-01-01", "allocation_in", "7000.00", "100", "70", flow));
        await SendAsync(app, HttpMethod.Post, $"{url}/holdings/{euro}/movements", Move("2023-12-31", "interest", "90.00"));
        await SendAsync(app, HttpMethod.Post, $"{url}/holdings/{fund}/movements", Move("2023-12-31", "fee", "61.60", "-0.8", "77"));
        await SendAsync(app, HttpMethod.Post, $"{url}/holding-snapshots", new
        {
            asOf = "2023-12-31", recordValuation = true, sourceLabel = "Relevé annuel 2023",
            lines = new object[] { new { holdingId = euro, value = Eur("3090.00") }, new { holdingId = fund, units = "99.2", unitPrice = "77", value = Eur("7638.40") } },
        });
        await SendAsync(app, HttpMethod.Post, $"{url}/switches", new
        {
            occurredOn = "2024-06-30", label = "Arbitrage", from = new { holdingId = fund, amount = Eur("1600.00"), units = "-20", unitPrice = "80" },
            to = new { holdingId = euro, amount = Eur("1600.00") },
        });
        await SendAsync(app, HttpMethod.Post, $"{url}/holdings/{euro}/movements", Move("2024-12-31", "interest", "116.70"));
        await SendAsync(app, HttpMethod.Post, $"{url}/holdings/{fund}/movements", Move("2024-12-31", "fee", "60.00", "-0.8", "75"));
        await SendAsync(app, HttpMethod.Post, $"{url}/holding-snapshots", new
        {
            asOf = "2024-12-31", complete = false,
            lines = new object[] { new { holdingId = euro, value = Eur("4806.70") }, new { holdingId = fund, units = "78.4", unitPrice = "75", value = Eur("5880.00") } },
        });
    }

    /// <summary>An envelope's holdings, snapshots, and each holding's movements and series, by holding name; lines by value (they come in id order).</summary>
    private static async Task<JsonObject> HoldingReadsAsync(NidApp app, string url)
    {
        var holdings = await SendAsync(app, HttpMethod.Get, $"{url}/holdings?on=2025-12-31");
        var snapshots = await SendAsync(app, HttpMethod.Get, $"{url}/holding-snapshots");
        foreach (var snapshot in snapshots["items"]?.AsArray() ?? [])
            snapshot!["lines"] = new JsonArray([.. snapshot["lines"]!.AsArray().Select(l => l!.DeepClone())
                .OrderBy(l => l["value"]!["amount"]!.GetValue<string>(), StringComparer.Ordinal)]);
        var result = new JsonObject { ["holdings"] = holdings, ["snapshots"] = snapshots };
        foreach (var holding in holdings["holdings"]!.AsArray().Select(h => h!.DeepClone()).ToList())
        {
            var holdingUrl = $"{url}/holdings/{holding["id"]!.GetValue<string>()}";
            result[holding["name"]!.GetValue<string>()] = new JsonObject
            {
                ["movements"] = await SendAsync(app, HttpMethod.Get, $"{holdingUrl}/movements"),
                ["series"] = await SendAsync(app, HttpMethod.Get, $"{holdingUrl}/series"),
            };
        }
        return result;
    }

    /// <summary>The envelope reads agree too: list, detail, series and performance, per envelope by name, without any id.</summary>
    private static async Task<string> InvestmentReadsAsync(NidApp app, PersonId reader)
    {
        var list = await EnvelopeListAsync(app);
        var byName = list.Select(e => (Name: e!["displayName"]!.GetValue<string>(), Id: e["id"]!.GetValue<string>()))
            .OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        var result = new JsonObject();
        var sorted = list.Select(e => e!.DeepClone()).OrderBy(e => e["displayName"]!.GetValue<string>(), StringComparer.Ordinal).ToArray();
        result["list"] = new JsonArray(sorted);
        foreach (var (name, id) in byName)
        {
            var url = $"/api/v1/investments/envelopes/{id}";
            result[name] = new JsonObject
            {
                ["detail"] = await SendAsync(app, HttpMethod.Get, url),
                ["series"] = await SendAsync(app, HttpMethod.Get, $"{url}/series"),
                ["performance"] = await SendAsync(app, HttpMethod.Get, $"{url}/performance?from=2024-01-01&to=2025-12-31"),
                ["holdings"] = await HoldingReadsAsync(app, url),
            };
        }
        // A holding's version is its row's xmin, a transaction id of the instance that wrote it.
        StripIds(result, "holdingId", "snapshotId", "envelopeFlowId", "envelopeId", "version");
        await NamePersonsAsync(app, result);
        return result.ToJsonString();
    }

    /// <summary>
    /// A person's id is the instance's own (the import maps people, it does not keep their ids): each `personId` read
    /// becomes the person's name, the same on both sides (« Adulte 1 », « Adulte 2 », an imported dependent's name).
    /// </summary>
    private static async Task NamePersonsAsync(NidApp app, JsonNode node)
    {
        var names = (await SendAsync(app, HttpMethod.Get, "/api/v1/core/persons")).AsArray()
            .ToDictionary(p => p!["id"]!.GetValue<string>(), p => p!["displayName"]!.GetValue<string>());
        Rename(node);

        void Rename(JsonNode? current)
        {
            if (current is JsonObject obj)
            {
                if (obj["personId"] is JsonValue id && id.TryGetValue<string>(out var value))
                    obj["personId"] = names.GetValueOrDefault(value, value);
                foreach (var (_, child) in obj.ToList())
                    Rename(child);
                // Owners come in their ids' order, which is the instance's own: compared by name.
                if (obj["owners"] is JsonArray owners)
                    obj["owners"] = new JsonArray([.. owners.Select(o => o!.DeepClone())
                        .OrderBy(o => o["personId"]?.ToJsonString(), StringComparer.Ordinal)]);
            }
            else if (current is JsonArray array)
                foreach (var child in array)
                    Rename(child);
        }
    }

    private static void StripIds(JsonNode? node, params string[] also)
    {
        if (node is JsonObject obj)
        {
            foreach (var name in new[] { "id", "itemId", "factId", "valuationId", "flowId" }.Concat(also))
                obj.Remove(name);
            foreach (var (_, child) in obj.ToList())
                StripIds(child, also);
        }
        else if (node is JsonArray array)
            foreach (var child in array)
                StripIds(child, also);
    }

    private const string IncomeSources = """
        [{"id":"inc-salary","name":"Salaire","kind":"salary","payer_label":"Employeur","currency":"EUR",
          "ownership":[{"person":"adult-1","share_bp":10000}],"visibility":"shared"}]
        """;

    private const string IncomePhases = """
        [{"id":"ip-1","source":"inc-salary","label":"Salaire de base","starts_on":"2024-09-01",
          "net_amount":{"amount":"2200.00","currency":"EUR"},"every":{"count":1,"unit":"month"}},
         {"id":"ip-2","source":"inc-salary","label":"Prime annuelle","starts_on":"2024-12-15",
          "net_amount":{"amount":"1800.00","currency":"EUR"},"every":{"count":1,"unit":"year"}}]
        """;

    /// <summary>A goal on a private envelope and one on a shared one (closed later through the API), a closed emergency fund, a free goal with entries.</summary>
    private const string GoalsJson = """
        [{"id":"goal-trip","name":"Voyage","kind":"savings","target_amount":"5000.00","target_date":"2026-06-30","note":"Japon",
          "ownership":[{"person":"adult-1","share_bp":10000}],"funding":[{"envelope":"env-pea","share_bp":5000},{"envelope":"env-livret","share_bp":10000}]},
         {"id":"goal-fund","name":"Précaution","kind":"emergency_fund","months":4,"closed":true,
          "ownership":[{"person":"adult-1","share_bp":10000}],"funding":[{"envelope":"env-pea","share_bp":5000}]},
         {"id":"goal-free","name":"Vélo","kind":"savings","target_amount":"800.00","ownership":[{"person":"adult-1","share_bp":10000}],
          "entries":[{"on":"2025-01-01","amount":"100.00"},{"on":"2025-06-01","amount":"250.00"}]}]
        """;

    /// <summary>What the interface adds on top of an imported source: a raise, a one-off, and a source with no import reference that is ended.</summary>
    private static async Task AddIncomeDataThroughTheApiAsync(NidApp app)
    {
        const string sources = "/api/v1/income/sources";
        var list = (await SendAsync(app, HttpMethod.Get, sources + "?scope=me"))["items"]!.AsArray();
        var salary = list.Single(s => s!["displayName"]!.GetValue<string>() == "Salaire")!["id"]!.GetValue<string>();
        await SendAsync(app, HttpMethod.Post, $"{sources}/{salary}/phase-changes",
            new { label = "Salaire de base", effectiveOn = "2025-09-01", netAmount = Eur("2300.00") });
        await SendAsync(app, HttpMethod.Post, $"{sources}/{salary}/one-offs",
            new { occurredOn = "2025-03-10", amount = Eur("500.00"), note = "Prime exceptionnelle" });
        var side = (await SendAsync(app, HttpMethod.Post, sources, new Dictionary<string, object?>
        {
            ["displayName"] = "Mission", ["kind"] = "self_employment", ["currency"] = "EUR", ["visibility"] = "shared",
            ["owners"] = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            ["phases"] = new[] { new { label = "Mission", startsOn = "2025-01-01", endsOn = (string?)null, netAmount = Eur("900.00"), every = new { count = 1, unit = "month" }, maxCycles = (int?)null } },
        }))["id"]!.GetValue<string>();
        await SendAsync(app, HttpMethod.Post, $"{sources}/{side}/close", new { endedOn = "2025-09-30" });
    }

    /// <summary>The income reads agree: list (by name), each detail and its expected income, without any id.</summary>
    private static async Task<string> IncomeReadsAsync(NidApp app)
    {
        var list = (await SendAsync(app, HttpMethod.Get, "/api/v1/income/sources?scope=me&archived=true"))["items"]!.AsArray();
        var result = new JsonObject();
        result["list"] = new JsonArray(list.Select(e => e!.DeepClone()).OrderBy(e => e["displayName"]!.GetValue<string>(), StringComparer.Ordinal).ToArray());
        foreach (var source in list.OrderBy(e => e!["displayName"]!.GetValue<string>(), StringComparer.Ordinal))
        {
            var url = $"/api/v1/income/sources/{source!["id"]!.GetValue<string>()}";
            result[source["displayName"]!.GetValue<string>()] = new JsonObject
            {
                ["detail"] = await SendAsync(app, HttpMethod.Get, url),
                ["expected"] = await SendAsync(app, HttpMethod.Get, $"{url}/expected?from=2025-01-01&to=2025-12-31"),
            };
        }
        StripIds(result, "phaseId", "version"); // a phase's concurrency token is its row's xmin, a transaction id of the instance that wrote it
        await NamePersonsAsync(app, result);
        return result.ToJsonString();
    }

    internal static string Canonical(Dictionary<string, List<JsonElement>> export)
        => JsonSerializer.Serialize(export.OrderBy(s => s.Key, StringComparer.Ordinal).ToDictionary(s => s.Key, s => s.Value));

    [Fact]
    public async Task An_export_imported_into_a_fresh_instance_exports_the_same_again()
    {
        await using var source = await NidApp.StartAsync(fixture);
        await PrepareAsync(source, "Anna");
        await CommitAsync(source, source.AdultA, GoldenFile.Sections());
        await CommitAsync(source, source.AdultA, new Dictionary<string, string>
        {
            ["persons"] = """[{"ref":"adult-1","kind":"adult","display_name":"x"},{"ref":"adult-2","kind":"adult","display_name":"y"},{"ref":"kid","kind":"dependent","display_name":"Léa"}]""",
            ["subscriptions"] = """
                [{"id":"sub-private","name":"Privé","visibility":"private","ownership":[{"person":"adult-1","share_bp":10000}],"auto_renew":false,"cancelled_at":"2025-09-01","note":"n"},
                 {"id":"sub-kid","name":"Kid","provider_key":"netflix","ownership":[{"person":"kid","share_bp":10000}],"service_users":["kid","adult-2"],"auto_renew":true},
                 {"id":"sub-duo","name":"Duo","ownership":[{"person":"adult-2","share_bp":4000},{"person":"adult-1","share_bp":6000}],"auto_renew":true}]
                """,
            ["billing_phases"] = """[{"id":"ph-kid","subscription_id":"sub-kid","starts_on":"2025-01-01","ends_on":null,"price":{"amount":"9.99","currency":"USD"},"every":{"count":1,"unit":"month"},"covers":{"count":2,"unit":"month"},"max_cycles":6,"payer":{"type":"person","person":"adult-2"}}]""",
            ["purchases"] = """[{"id":"pu-mixed","date":"2025-02-01","label":"Mix","kind":"bundle","price":{"amount":"30.00","currency":"EUR"},"payer":{"type":"household"},"allocated_amount":"20.00","grants":[{"subscription_id":"sub-duo","duration":{"count":1,"unit":"month"},"source":"included"},{"subscription_id":"sub-private","duration":{"count":1,"unit":"month"},"source":"included","starts_on":"2025-02-01"}]}]""",
            ["envelopes"] = EnvelopesImportTests.Envelopes.Replace(",\"closed_on\":\"2025-07-31\"", ""),
            ["valuations"] = WithoutLivretClosing(FactsImportTests.Valuations, "v-livret-0"),
            ["flows"] = WithoutLivretClosing(FactsImportTests.Flows, "f-livret-out"),
            ["income_sources"] = IncomeSources,
            ["income_phases"] = IncomePhases,
            ["goals"] = GoalsJson,
        });
        await AddEnvelopeDataThroughTheApiAsync(source);
        await AddHoldingsThroughTheApiAsync(source);
        await AddIncomeDataThroughTheApiAsync(source);
        // An adopted provider (only a migration makes one) named by a subscription created in the interface.
        await using (var subs = PostgresFixture.NewSubscriptionsContext(source.ConnectionString))
        {
            subs.Providers.Add(new Nid.Subscriptions.Infrastructure.Persistence.ProviderRow { Key = "Old Key!", Origin = "adopted", Name = "Old", LabelKeys = ["other"] });
            await subs.SaveChangesAsync(TestContext.Current.CancellationToken);
            await subs.Database.ExecuteSqlRawAsync("update subscriptions.subscription set provider_key = 'Old Key!' where import_ref = 'sub-duo'", cancellationToken: TestContext.Current.CancellationToken);
        }

        var first = await new ExportClient(source).ExportAllAsync(source.AdultA);

        await using var target = await NidApp.StartAsync(fixture);
        await PrepareAsync(target, "Zoé");
        await CommitAsync(target, target.AdultA, AsFile(first));
        var second = await new ExportClient(target).ExportAllAsync(target.AdultA);

        Assert.Equal(Canonical(first), Canonical(second));
        Assert.Equal(await ReadsAsync(source, source.AdultA), await ReadsAsync(target, target.AdultA));
        Assert.Equal(await InvestmentReadsAsync(source, source.AdultA), await InvestmentReadsAsync(target, target.AdultA));
        Assert.Equal(await IncomeReadsAsync(source), await IncomeReadsAsync(target));
        Assert.Equal(6, first["subscriptions"].Count);
        Assert.Equal(3, first["goals"].Count);
        Assert.Contains(first["goals"], g => g.GetProperty("funding").GetArrayLength() == 2 && g.GetProperty("entries").GetArrayLength() == 0);
        Assert.Contains(first["goals"], g => g.GetProperty("entries").GetArrayLength() == 2);
        Assert.Equal(4, first["envelopes"].Count);
        // The comparison is not vacuous: the API-made facts are in the reads and in the file.
        var reads = await InvestmentReadsAsync(target, target.AdultA);
        Assert.Contains("25500", reads);
        Assert.Contains("Crypto", reads);
        Assert.Contains(first["envelopes"], e => e.GetProperty("name").GetString() == "Livret A" && e.TryGetProperty("closed_on", out var c) && c.ValueKind == JsonValueKind.String);
        var livret = JsonNode.Parse(reads)!["Livret A"]!["detail"]!;
        Assert.True(livret["closed"]!.GetValue<bool>());
        Assert.Contains(first["flows"], f => f.GetProperty("external_ref").GetString()?.StartsWith("transfer:") == true);
        // The holdings: both lines, both snapshots, the switch's two legs under one reference, the fees, and the allocations' flow.
        Assert.Equal(["Fonds A", "Fonds euros"], first["holdings"].Select(h => h.GetProperty("name").GetString()));
        Assert.Equal(["2023-12-31", "2024-12-31"], first["holding_snapshots"].Select(s => s.GetProperty("as_of").GetString()));
        Assert.Equal(8, first["holding_movements"].Count);
        Assert.Single(first["holding_movements"].Where(m => m.GetProperty("switch_ref").ValueKind == JsonValueKind.String)
            .GroupBy(m => m.GetProperty("switch_ref").GetString()), g => g.Count() == 2);
        Assert.Equal(2, first["holding_movements"].Count(m => m.GetProperty("kind").GetString() == "fee"));
        var contribution = first["flows"].Single(f => f.GetProperty("amount").GetProperty("amount").GetString() == "10000.0000"
            && f.GetProperty("occurred_on").GetString() == "2023-01-01").GetProperty("id").GetString();
        Assert.Equal(2, first["holding_movements"].Count(m => m.GetProperty("envelope_flow").GetString() == contribution));
        var holdingReads = JsonNode.Parse(reads)!["Assurance-vie"]!["holdings"]!;
        Assert.Equal("121.6000", holdingReads["holdings"]!["explicitFees"]!["amount"]!.GetValue<string>());
        Assert.Equal(4, holdingReads["Fonds A"]!["movements"]!["items"]!.AsArray().Count);
        // Same for income: the API-made raise, one-off and ended source are in the reads and in the file.
        var income = await IncomeReadsAsync(target);
        Assert.Contains("2300", income);
        Assert.Contains("Prime exceptionnelle", income);
        Assert.Equal(2, first["income_sources"].Count);
        Assert.Contains(first["income_sources"], s => s.TryGetProperty("ended_on", out var e) && e.ValueKind == JsonValueKind.String);
        Assert.Contains(first["income_phases"], p => p.GetProperty("label").GetString() == "Salaire de base" && p.GetProperty("starts_on").GetString() == "2025-09-01");
        Assert.Contains(first["flows"], f => f.TryGetProperty("category", out var c) && c.GetString() == "one_off");
    }
}
