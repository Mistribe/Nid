using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Host.IntegrationTests.Import;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Export;

public class ExportPrivacyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task The_other_adults_private_edits_do_not_move_the_readers_revision()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var validated = await client.RunAsync(app.AdultA,
            new Dictionary<string, Guid> { ["adult-1"] = app.AdultA.Value, ["adult-2"] = app.AdultB.Value },
            new Dictionary<string, string>
            {
                ["persons"] = GoldenFile.Persons,
                ["subscriptions"] = """
                    [{"id":"sub-shared","name":"Shared","ownership":[{"person":"adult-1","share_bp":10000}],"auto_renew":true},
                     {"id":"sub-private","name":"Private","visibility":"private","ownership":[{"person":"adult-1","share_bp":10000}],"auto_renew":true}]
                    """,
            });
        Assert.True((await client.CommitRawAsync(app.AdultA, validated.Id)).IsSuccessStatusCode);
        var export = new ExportClient(app);
        var before = await export.ManifestAsync(app.AdultB);
        var mineBefore = await export.ManifestAsync(app.AdultA);

        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
        {
            (await core.Items.SingleAsync(i => i.DisplayName == "Private", cancellationToken: TestContext.Current.CancellationToken)).DisplayName = "Private, renamed";
            await core.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(before.Revision, (await export.ManifestAsync(app.AdultB)).Revision);
        Assert.NotEqual(mineBefore.Revision, (await export.ManifestAsync(app.AdultA)).Revision);
    }

    [Fact]
    public async Task A_page_after_a_change_the_reader_can_see_is_refused_as_stale()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var persons = """[{"ref":"adult-1","kind":"adult","display_name":"A"},{"ref":"k1","kind":"dependent","display_name":"K1"},{"ref":"k2","kind":"dependent","display_name":"K2"}]""";
        var validated = await client.RunAsync(app.AdultA, new Dictionary<string, Guid> { ["adult-1"] = app.AdultA.Value },
            new Dictionary<string, string>
            {
                ["persons"] = persons,
                ["subscriptions"] = """[{"id":"s1","name":"S1","ownership":[{"person":"k1","share_bp":10000}],"service_users":["k2"],"auto_renew":true}]""",
            });
        Assert.True((await client.CommitRawAsync(app.AdultA, validated.Id)).IsSuccessStatusCode);
        var export = new ExportClient(app);
        var first = await export.PageAsync(app.AdultA, "persons", limit: 1);

        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
        {
            (await core.Items.SingleAsync(i => i.DisplayName == "S1", cancellationToken: TestContext.Current.CancellationToken)).DisplayName = "S1, renamed";
            await core.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var stale = await export.PageRawAsync(app.AdultA, "persons", first.NextCursor, 1);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, stale.StatusCode);
    }

    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static async Task<Guid> CreateEnvelopeAsync(NidApp app, PersonId creator, string name, string visibility, params (PersonId Owner, int Bp)[] owners)
    {
        var response = await app.RawAsync(creator, HttpMethod.Post, "/api/v1/investments/envelopes", new
        {
            displayName = name, kind = "savings", currency = "EUR", visibility,
            owners = owners.Select(o => new { personId = o.Owner.Value, shareBp = o.Bp }).ToArray(),
            firstValuation = new { asOf = "2025-06-30", amount = Eur("1000.00") },
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Each_adult_exports_only_the_envelopes_and_facts_they_read_and_a_transfer_to_a_private_envelope_keeps_only_the_readers_leg()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var p2 = await CreateEnvelopeAsync(app, app.AdultB, "P2-secret", "private", (app.AdultB, 10_000));
        var s1 = await CreateEnvelopeAsync(app, app.AdultA, "S1-shared", "shared", (app.AdultA, 5_000), (app.AdultB, 5_000));
        var transfer = await app.RawAsync(app.AdultB, HttpMethod.Post, "/api/v1/investments/transfers",
            new { from = p2, to = s1, occurredOn = "2025-07-01", amount = Eur("100.00") });
        Assert.True(transfer.IsSuccessStatusCode, await transfer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var transferId = JsonDocument.Parse(await transfer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("id").GetGuid();
        var export = new ExportClient(app);

        var forA = await export.ExportAllAsync(app.AdultA);
        var forB = await export.ExportAllAsync(app.AdultB);

        var envelopeA = Assert.Single(forA["envelopes"]);
        Assert.Equal("S1-shared", envelopeA.GetProperty("name").GetString());
        var envelopeId = envelopeA.GetProperty("id").GetString();
        Assert.All(forA["valuations"], v => Assert.Equal(envelopeId, v.GetProperty("item").GetString()));
        Assert.Single(forA["valuations"]);
        var leg = Assert.Single(forA["flows"]);
        Assert.Equal(envelopeId, leg.GetProperty("item").GetString());
        Assert.Equal("contribution", leg.GetProperty("kind").GetString());
        Assert.Equal($"transfer:{transferId}", leg.GetProperty("external_ref").GetString());
        var everything = JsonSerializer.Serialize(forA);
        Assert.DoesNotContain("P2-secret", everything);
        Assert.DoesNotContain(p2.ToString(), everything);
        Assert.DoesNotContain(p2.ToString("N"), everything);

        Assert.Equal(["P2-secret", "S1-shared"], forB["envelopes"].Select(e => e.GetProperty("name").GetString()!).Order(StringComparer.Ordinal));
        Assert.Equal(2, forB["valuations"].Count);
        Assert.Equal(["contribution", "withdrawal"], forB["flows"].Select(f => f.GetProperty("kind").GetString()!).Order(StringComparer.Ordinal));
        Assert.All(forB["flows"], f => Assert.Equal($"transfer:{transferId}", f.GetProperty("external_ref").GetString()));
    }

    [Fact]
    public async Task The_other_adults_private_income_source_with_its_phase_and_one_off_is_not_exported()
    {
        await using var app = await NidApp.StartAsync(fixture);
        object Money(string amount) => new { amount, currency = "EUR" };
        var created = await app.RawAsync(app.AdultB, HttpMethod.Post, "/api/v1/income/sources", new Dictionary<string, object?>
        {
            ["displayName"] = "Revenu-secret", ["kind"] = "salary", ["currency"] = "EUR", ["visibility"] = "private",
            ["owners"] = new[] { new { personId = app.AdultB.Value, shareBp = 10_000 } },
            ["phases"] = new[] { new { label = "Phase-secrete", startsOn = "2025-01-01", endsOn = (string?)null, netAmount = Money("2000"), every = new { count = 1, unit = "month" }, maxCycles = (int?)null } },
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("id").GetGuid();
        var oneOff = await app.RawAsync(app.AdultB, HttpMethod.Post, $"/api/v1/income/sources/{id}/one-offs",
            new { occurredOn = "2025-06-10", amount = Money("500"), note = "Prime-secrete" });
        Assert.True(oneOff.IsSuccessStatusCode, await oneOff.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var export = new ExportClient(app);

        var forA = await export.ExportAllAsync(app.AdultA);
        var forB = await export.ExportAllAsync(app.AdultB);

        Assert.Empty(forA.GetValueOrDefault("income_sources") ?? []);
        Assert.Empty(forA.GetValueOrDefault("income_phases") ?? []);
        Assert.Empty(forA.GetValueOrDefault("flows") ?? []);
        var everything = JsonSerializer.Serialize(forA);
        foreach (var secret in new[] { "Revenu-secret", "Phase-secrete", "Prime-secrete", id.ToString(), id.ToString("N") })
            Assert.DoesNotContain(secret, everything);
        Assert.Single(forB["income_sources"]);
        Assert.Single(forB["income_phases"]);
        Assert.Single(forB["flows"]);
    }
}
