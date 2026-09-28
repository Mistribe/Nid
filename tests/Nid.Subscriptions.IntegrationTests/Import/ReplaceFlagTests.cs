using System.Net;
using Microsoft.EntityFrameworkCore;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Import;

[Collection("postgres")]
public class ReplaceFlagTests(PostgresFixture fixture)
{
    private static object Adults(NidApp app) => new Dictionary<string, Guid> { ["adult-1"] = app.AdultA.Value, ["adult-2"] = app.AdultB.Value };

    [Fact]
    public async Task A_report_counts_replaced_and_it_is_zero_without_replace()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var validated = await client.RunAsync(app.AdultA, Adults(app), GoldenFile.Sections());

        Assert.Equal("validated", validated.State);
        Assert.All(validated.Report!.Sections.Values, counts => Assert.Equal(0, counts.Replaced));
    }

    [Fact]
    public async Task The_commit_writes_as_the_importer_through_import()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var validated = await client.RunAsync(app.AdultA, Adults(app), GoldenFile.Sections());
        Assert.True((await client.CommitRawAsync(app.AdultA, validated.Id)).IsSuccessStatusCode);

        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var writes = await platform.AuditLog.Where(r => r.Category == "write" && r.Request == "CommitImportCommand").ToListAsync();
        Assert.NotEmpty(writes);
        Assert.All(writes, w => Assert.Equal(("person", app.AdultA.Value, "import"), (w.ActorKind, w.PersonId!.Value, w.Via)));
    }

    [Fact]
    public async Task Validate_and_commit_accept_an_empty_body_or_replace_false()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA, Adults(app));
        foreach (var (section, entities) in GoldenFile.Sections())
            Assert.True((await client.PutAsync(app.AdultA, session.Id, section, 0, entities)).Accepted);

        Assert.True((await client.ValidateRawAsync(app.AdultA, session.Id, replace: false)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.CommitRawAsync(app.AdultA, session.Id)).StatusCode);
    }

    [Fact]
    public async Task A_session_validated_with_replace_then_committed_without_it_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var first = await client.RunAsync(app.AdultA, Adults(app), GoldenFile.Sections());
        Assert.True((await client.CommitRawAsync(app.AdultA, first.Id)).IsSuccessStatusCode);

        var changed = GoldenFile.With("subscriptions", GoldenFile.Subscriptions.Replace("Spotify Premium Duo", "Spotify Family"));
        var session = await client.StartAsync(app.AdultA, Adults(app));
        foreach (var (section, entities) in changed)
            Assert.True((await client.PutAsync(app.AdultA, session.Id, section, 0, entities)).Accepted);
        var validated = await client.ValidateAsync(app.AdultA, session.Id, replace: true);
        Assert.Equal("validated", validated.State);

        var refused = await client.CommitRawAsync(app.AdultA, session.Id, replace: false);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(ImportCodes.NotCommitted, await ImportClient.CodeOf(refused));
        Assert.Equal("invalid", (await client.GetAsync(app.AdultA, session.Id)).State);
    }
}
