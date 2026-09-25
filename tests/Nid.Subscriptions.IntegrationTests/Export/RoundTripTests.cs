using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.IntegrationTests.Import;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Export;

/// <summary>The criterion of 1D2 (stage-1 overview): an export re-imported into a fresh instance exports the same again.</summary>
[Collection("postgres")]
public class RoundTripTests(PostgresFixture fixture)
{
    /// <summary>Same catalog, same adult names in both instances: what the file carries must be the same on both sides.</summary>
    private static async Task PrepareAsync(NidApp app)
    {
        app.Catalog.Serve(CatalogFixtures.RealLabels(), CatalogFixtures.RealProviders());
        await using (var scope = app.Services.CreateAsyncScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<CatalogSync>().RunAsync(default)).TryGetValue(out _));
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        (await core.Persons.SingleAsync(p => p.Id == app.AdultA.Value)).DisplayName = "Adulte 1";
        (await core.Persons.SingleAsync(p => p.Id == app.AdultB.Value)).DisplayName = "Adulte 2";
        // The seeded dependent's name is random, and person refs are numbered over every person (§5.4).
        (await core.Persons.SingleAsync(p => p.Id == app.Dependent.Value)).DisplayName = "Zoé";
        await core.SaveChangesAsync();
    }

    private static object Adults(NidApp app) => new Dictionary<string, Guid> { ["adult-1"] = app.AdultA.Value, ["adult-2"] = app.AdultB.Value };

    private static async Task CommitAsync(NidApp app, PersonId actor, IReadOnlyDictionary<string, string> sections)
    {
        var client = new ImportClient(app);
        var validated = await client.RunAsync(actor, Adults(app), sections);
        Assert.True(validated.State == "validated", string.Join("; ", validated.Report!.Issues.Select(i => $"{i.Code} at {i.Path}: {i.Message}")));
        var committed = await client.CommitRawAsync(actor, validated.Id);
        Assert.True(committed.IsSuccessStatusCode, await committed.Content.ReadAsStringAsync());
    }

    private static Dictionary<string, string> AsFile(Dictionary<string, List<JsonElement>> export)
        => export.Where(s => s.Value.Count > 0).ToDictionary(s => s.Key, s => JsonSerializer.Serialize(s.Value));

    private static string Canonical(Dictionary<string, List<JsonElement>> export)
        => JsonSerializer.Serialize(export.OrderBy(s => s.Key, StringComparer.Ordinal).ToDictionary(s => s.Key, s => s.Value));

    [Fact]
    public async Task An_export_imported_into_a_fresh_instance_exports_the_same_again()
    {
        await using var source = await NidApp.StartAsync(fixture);
        await PrepareAsync(source);
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
        });
        // An adopted provider (only a migration makes one) named by a subscription created in the interface.
        await using (var subs = PostgresFixture.NewSubscriptionsContext(source.ConnectionString))
        {
            subs.Providers.Add(new Infrastructure.Persistence.ProviderRow { Key = "Old Key!", Origin = "adopted", Name = "Old", LabelKeys = ["other"] });
            await subs.SaveChangesAsync();
            await subs.Database.ExecuteSqlRawAsync("update subscriptions.subscription set provider_key = 'Old Key!' where import_ref = 'sub-duo'");
        }

        var first = await new ExportClient(source).ExportAllAsync(source.AdultA);

        await using var target = await NidApp.StartAsync(fixture);
        await PrepareAsync(target);
        await CommitAsync(target, target.AdultA, AsFile(first));
        var second = await new ExportClient(target).ExportAllAsync(target.AdultA);

        Assert.Equal(Canonical(first), Canonical(second));
        Assert.Equal(6, first["subscriptions"].Count);
    }
}
