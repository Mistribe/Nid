using System.Net;
using Nid.Subscriptions.IntegrationTests.Import;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Export;

[Collection("postgres")]
public class ExportRouteTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_manifest_lists_every_section_in_file_order()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var manifest = await new ExportClient(app).ManifestAsync(app.AdultA);

        Assert.Equal(("nid.data", 1), (manifest.Format, manifest.Version));
        Assert.Equal(["persons", "providers", "subscriptions", "billing_phases", "purchases", "envelopes", "valuations", "flows"], manifest.Sections.Select(s => s.Name));
        Assert.Matches("^[0-9a-f]{64}$", manifest.Revision);
    }

    [Fact]
    public async Task Pages_follow_their_cursor_to_the_end()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var persons = """[{"ref":"adult-1","kind":"adult","display_name":"A"},{"ref":"k1","kind":"dependent","display_name":"K1"},{"ref":"k2","kind":"dependent","display_name":"K2"},{"ref":"k3","kind":"dependent","display_name":"K3"}]""";
        var subscriptions = """[{"id":"s1","name":"S1","ownership":[{"person":"k1","share_bp":10000}],"service_users":["k2","k3"],"auto_renew":true}]""";
        var client = new ImportClient(app);
        var validated = await client.RunAsync(app.AdultA, new Dictionary<string, Guid> { ["adult-1"] = app.AdultA.Value },
            new Dictionary<string, string> { ["persons"] = persons, ["subscriptions"] = subscriptions });
        Assert.True((await client.CommitRawAsync(app.AdultA, validated.Id)).IsSuccessStatusCode);
        var export = new ExportClient(app);

        var first = await export.PageAsync(app.AdultA, "persons", limit: 2);
        var second = await export.PageAsync(app.AdultA, "persons", first.NextCursor, limit: 2);

        Assert.Equal(2, first.Entities.Count);
        Assert.NotNull(first.NextCursor);
        Assert.Single(second.Entities);
        Assert.Null(second.NextCursor);
    }

    [Theory]
    [InlineData("not-a-cursor", HttpStatusCode.BadRequest, "export.bad_cursor")]
    [InlineData("eyJyIjoiMDAwMCIsIm8iOjB9", HttpStatusCode.Conflict, "export.revision_changed")] // {"r":"0000","o":0}
    [InlineData("eyJyIjoiMDAwMCIsIm8iOjk5OX0", HttpStatusCode.Conflict, "export.revision_changed")] // {"r":"0000","o":999}: stale first, even past the end
    public async Task A_bad_or_stale_cursor_is_refused(string cursor, HttpStatusCode status, string code)
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await new ExportClient(app).PageRawAsync(app.AdultA, "subscriptions", cursor);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await ImportClient.CodeOf(response));
    }

    [Fact]
    public async Task An_unknown_section_is_not_found()
    {
        await using var app = await NidApp.StartAsync(fixture);

        Assert.Equal(HttpStatusCode.NotFound, (await new ExportClient(app).PageRawAsync(app.AdultA, "documents")).StatusCode);
    }
}
