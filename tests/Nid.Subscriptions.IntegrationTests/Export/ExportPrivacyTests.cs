using Microsoft.EntityFrameworkCore;
using Nid.Subscriptions.IntegrationTests.Import;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Export;

[Collection("postgres")]
public class ExportPrivacyTests(PostgresFixture fixture)
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
            (await core.Items.SingleAsync(i => i.DisplayName == "Private")).DisplayName = "Private, renamed";
            await core.SaveChangesAsync();
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
            (await core.Items.SingleAsync(i => i.DisplayName == "S1")).DisplayName = "S1, renamed";
            await core.SaveChangesAsync();
        }

        var stale = await export.PageRawAsync(app.AdultA, "persons", first.NextCursor, 1);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, stale.StatusCode);
    }
}
