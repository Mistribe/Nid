using System.Net;
using Microsoft.EntityFrameworkCore;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Import;

[Collection("postgres")]
public class ReplaceSubscriptionsTests(PostgresFixture fixture)
{
    private static object Adults(NidApp app) => new Dictionary<string, Guid> { ["adult-1"] = app.AdultA.Value, ["adult-2"] = app.AdultB.Value };

    private static async Task<SessionResponse> CommitAsync(ImportClient client, NidApp app, IReadOnlyDictionary<string, string> sections, bool replace = false)
    {
        var session = await client.StartAsync(app.AdultA, Adults(app));
        foreach (var (section, entities) in sections)
            Assert.True((await client.PutAsync(app.AdultA, session.Id, section, 0, entities)).Accepted);
        var validated = await client.ValidateAsync(app.AdultA, session.Id, replace);
        Assert.True(validated.State == "validated", string.Join("; ", validated.Report!.Issues.Select(i => $"{i.Code} at {i.Path}")));
        var committed = await client.CommitRawAsync(app.AdultA, session.Id, replace);
        Assert.True(committed.IsSuccessStatusCode, await committed.Content.ReadAsStringAsync());
        return await client.GetAsync(app.AdultA, session.Id);
    }

    private static IReadOnlyDictionary<string, string> Renamed()
        => GoldenFile.With("subscriptions", GoldenFile.Subscriptions.Replace("Spotify Premium Duo", "Spotify Family"));

    /// <summary>
    /// The fixture of <c>SubscriptionsImportTests.An_adult_cannot_add_to_an_imported_subscription_the_other_adult_writes_alone</c>:
    /// A imports "sub-a" owned by adult-1 alone. B can read it (household visibility) but not write it. B then
    /// re-imports the same id renamed, with <c>replace</c>: the renamed content differs from the stored hash, so
    /// this is a replacement, and B lacks the write access it needs.
    /// </summary>
    private static async Task<SessionResponse> ReimportAsBWithReplaceAsync(ImportClient client, NidApp app)
    {
        const string subscription = """[{"id":"sub-a","name":"A's","ownership":[{"person":"adult-1","share_bp":10000}],"auto_renew":true}]""";
        var first = await client.StartAsync(app.AdultA, Adults(app));
        Assert.True((await client.PutAsync(app.AdultA, first.Id, "persons", 0, GoldenFile.Persons)).Accepted);
        Assert.True((await client.PutAsync(app.AdultA, first.Id, "subscriptions", 0, subscription)).Accepted);
        var validatedFirst = await client.ValidateAsync(app.AdultA, first.Id);
        Assert.Equal("validated", validatedFirst.State);
        Assert.True((await client.CommitRawAsync(app.AdultA, first.Id)).IsSuccessStatusCode);

        const string renamed = """[{"id":"sub-a","name":"A's renamed","ownership":[{"person":"adult-1","share_bp":10000}],"auto_renew":true}]""";
        var session = await client.StartAsync(app.AdultB, Adults(app));
        Assert.True((await client.PutAsync(app.AdultB, session.Id, "persons", 0, GoldenFile.Persons)).Accepted);
        Assert.True((await client.PutAsync(app.AdultB, session.Id, "subscriptions", 0, renamed)).Accepted);
        return await client.ValidateAsync(app.AdultB, session.Id, replace: true);
    }

    [Fact]
    public async Task With_replace_the_subscription_takes_the_files_content_and_its_history_keeps_the_old_name()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, GoldenFile.Sections());

        var committed = await CommitAsync(client, app, Renamed(), replace: true);

        Assert.Equal(1, committed.Report!.Sections["subscriptions"].Replaced);
        Assert.Equal(0, committed.Report.Sections["subscriptions"].Conflict);
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.True(await core.Items.AnyAsync(i => i.DisplayName == "Spotify Family"));
        Assert.False(await core.Items.AnyAsync(i => i.DisplayName == "Spotify Premium Duo"));
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var rename = await platform.AuditLog.SingleAsync(r => r.Category == "write" && r.Action == "update" && r.Entity == "core.item");
        Assert.Contains("Spotify Premium Duo", rename.Data);
        Assert.Equal("import", rename.Via);
    }

    [Fact]
    public async Task The_same_file_after_a_replacement_is_unchanged_and_writes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, GoldenFile.Sections());
        await CommitAsync(client, app, Renamed(), replace: true);
        await using var before = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var writes = await before.AuditLog.CountAsync(r => r.Category == "write");

        var again = await CommitAsync(client, app, Renamed(), replace: true);

        Assert.All(again.Report!.Sections.Values, c => Assert.Equal((0, 0, 0), (c.Created, c.Replaced, c.Conflict)));
        await using var after = PostgresFixture.NewPlatformContext(app.ConnectionString);
        Assert.Equal(writes, await after.AuditLog.CountAsync(r => r.Category == "write"));
    }

    [Fact]
    public async Task Without_write_access_the_replacement_is_not_permitted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var validated = await ReimportAsBWithReplaceAsync(client, app);

        Assert.Equal("invalid", validated.State);
        var refused = Assert.Single(validated.Report!.Issues, i => i.Code == ImportCodes.NotPermitted);
        Assert.Equal("subscriptions[0]", refused.Path);
    }

    [Fact]
    public async Task A_replacement_whose_owners_break_the_rules_is_refused_and_nothing_is_written()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, GoldenFile.Sections());
        // the golden subscriptions' ownership, with shares that no longer sum to 10 000
        var broken = GoldenFile.With("subscriptions", GoldenFile.Subscriptions.Replace("10000", "4000"));
        var session = await client.StartAsync(app.AdultA, Adults(app));
        foreach (var (section, entities) in broken)
            Assert.True((await client.PutAsync(app.AdultA, session.Id, section, 0, entities)).Accepted);

        var validated = await client.ValidateAsync(app.AdultA, session.Id, replace: true);

        Assert.Equal("invalid", validated.State);
        Assert.Contains(validated.Report!.Issues, i => i.Path.StartsWith("subscriptions[0].ownership", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Conflict, (await client.CommitRawAsync(app.AdultA, session.Id, replace: true)).StatusCode);
    }

    [Fact]
    public async Task A_replacement_that_removes_the_importer_from_the_owners_is_allowed_with_write_access()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, GoldenFile.Sections());
        // the golden sub-spotify record alone, owned by adult-1 (AdultA, the importer), handed over to adult-2;
        // matched with the trailing "service_users" field so only sub-spotify's ownership changes.
        var handedOver = GoldenFile.With("subscriptions", GoldenFile.Subscriptions.Replace(
            "\"person\":\"adult-1\",\"share_bp\":10000}],\"service_users\"",
            "\"person\":\"adult-2\",\"share_bp\":10000}],\"service_users\""));

        var committed = await CommitAsync(client, app, handedOver, replace: true);

        Assert.Equal(1, committed.Report!.Sections["subscriptions"].Replaced);
    }
}
