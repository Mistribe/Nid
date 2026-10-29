using System.Net;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Import;

public class ReplaceSubscriptionsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
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

    /// <summary>
    /// The golden sub-spotify record renamed, with its auto_renew and note also changed (a rename alone would
    /// not prove the store carries the rest of a replacement's fields): matched together with the trailing
    /// "service_users" field so only sub-spotify changes.
    /// </summary>
    private static IReadOnlyDictionary<string, string> Renamed()
        => GoldenFile.With("subscriptions", GoldenFile.Subscriptions
            .Replace("Spotify Premium Duo", "Spotify Family")
            .Replace(
                "\"service_users\":[\"adult-1\"],\"auto_renew\":true",
                "\"service_users\":[\"adult-1\"],\"auto_renew\":false,\"note\":\"Upgraded to family plan\""));

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
        Guid idBefore;
        string? providerKeyBefore;
        await using (var subsBefore = PostgresFixture.NewSubscriptionsContext(app.ConnectionString))
        {
            var before = await subsBefore.Subscriptions.AsNoTracking().SingleAsync(s => s.ImportRef == "sub-spotify");
            idBefore = before.ItemId;
            providerKeyBefore = before.ProviderKey;
        }

        var committed = await CommitAsync(client, app, Renamed(), replace: true);

        Assert.Equal(1, committed.Report!.Sections["subscriptions"].Replaced);
        Assert.Equal(0, committed.Report.Sections["subscriptions"].Conflict);
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.True(await core.Items.AnyAsync(i => i.DisplayName == "Spotify Family"));
        Assert.False(await core.Items.AnyAsync(i => i.DisplayName == "Spotify Premium Duo"));

        // The row is the same one, updated in place, taking the file's new auto_renew/note; its provider (a
        // generated key, not "spotify" itself — ProvidersImportTests) carries over unchanged.
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        var after = await subs.Subscriptions.AsNoTracking().SingleAsync(s => s.ImportRef == "sub-spotify");
        Assert.Equal(idBefore, after.ItemId);
        Assert.False(after.AutoRenew);
        Assert.Equal("Upgraded to family plan", after.Note);
        Assert.Equal(providerKeyBefore, after.ProviderKey);

        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var rename = await platform.AuditLog.SingleAsync(r => r.Category == "write" && r.Action == "update" && r.Entity == "core.item");
        Assert.Contains("Spotify Premium Duo", rename.Data);
        Assert.Equal("import", rename.Via);
        var subscriptionWrite = await platform.AuditLog.SingleAsync(
            r => r.Category == "write" && r.Action == "update" && r.Entity == "subscriptions.subscription");
        Assert.Equal("import", subscriptionWrite.Via);
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
        await using var before = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var writesBefore = await before.AuditLog.CountAsync(r => r.Category == "write");

        // the golden subscriptions' ownership, with shares that no longer sum to 10 000
        var broken = GoldenFile.With("subscriptions", GoldenFile.Subscriptions.Replace("10000", "4000"));
        var session = await client.StartAsync(app.AdultA, Adults(app));
        foreach (var (section, entities) in broken)
            Assert.True((await client.PutAsync(app.AdultA, session.Id, section, 0, entities)).Accepted);

        var validated = await client.ValidateAsync(app.AdultA, session.Id, replace: true);

        Assert.Equal("invalid", validated.State);
        Assert.Contains(validated.Report!.Issues, i => i.Path.StartsWith("subscriptions[0].ownership", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Conflict, (await client.CommitRawAsync(app.AdultA, session.Id, replace: true)).StatusCode);

        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var spotify = await core.Items.AsNoTracking().Include(i => i.Owners).SingleAsync(i => i.DisplayName == "Spotify Premium Duo");
        var owner = Assert.Single(spotify.Owners);
        Assert.Equal((app.AdultA.Value, 10000), (owner.PersonId, owner.ShareBp));
        await using var after = PostgresFixture.NewPlatformContext(app.ConnectionString);
        Assert.Equal(writesBefore, await after.AuditLog.CountAsync(r => r.Category == "write"));
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
    /// <summary>A shared subscription both adults own and write, imported by A (adult-1).</summary>
    private const string CoOwned = """[{"id":"sub-shared","name":"Shared","ownership":[{"person":"adult-1","share_bp":5000},{"person":"adult-2","share_bp":5000}],"auto_renew":true}]""";

    private static async Task<SessionResponse> StageAsync(ImportClient client, NidApp app, PersonId actor, string subscriptions)
    {
        var session = await client.StartAsync(actor, Adults(app));
        Assert.True((await client.PutAsync(actor, session.Id, "persons", 0, GoldenFile.Persons)).Accepted);
        Assert.True((await client.PutAsync(actor, session.Id, "subscriptions", 0, subscriptions)).Accepted);
        return session;
    }

    private static async Task<int> WritesAsync(NidApp app)
    {
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        return await platform.AuditLog.CountAsync(r => r.Category == "write");
    }

    /// <summary>User ruling 2026-09-28: a replacement does not take an item away from another adult (spec §3.2).</summary>
    [Fact]
    public async Task A_replacement_that_drops_another_adult_owner_is_not_permitted_and_writes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, new Dictionary<string, string> { ["persons"] = GoldenFile.Persons, ["subscriptions"] = CoOwned });
        var writesBefore = await WritesAsync(app);

        // B, a co-owner who writes it, re-imports it with B alone as owner: A would lose it.
        var session = await StageAsync(client, app, app.AdultB,
            CoOwned.Replace("""{"person":"adult-1","share_bp":5000},{"person":"adult-2","share_bp":5000}""", """{"person":"adult-2","share_bp":10000}"""));
        var validated = await client.ValidateAsync(app.AdultB, session.Id, replace: true);

        Assert.Equal("invalid", validated.State);
        var issue = Assert.Single(validated.Report!.Issues, i => i.Severity == "error");
        Assert.Equal((ImportCodes.NotPermitted, "subscriptions[0].ownership"), (issue.Code, issue.Path));
        Assert.Equal(0, validated.Report.Sections["subscriptions"].Replaced);
        Assert.Equal(HttpStatusCode.Conflict, (await client.CommitRawAsync(app.AdultB, session.Id, replace: true)).StatusCode);
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var item = await core.Items.AsNoTracking().Include(i => i.Owners).SingleAsync(i => i.DisplayName == "Shared");
        Assert.Equal(2, item.Owners.Count);
        Assert.Equal(writesBefore, await WritesAsync(app));
    }

    [Fact]
    public async Task A_replacement_that_keeps_the_other_adult_as_owner_may_change_the_shares()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, new Dictionary<string, string> { ["persons"] = GoldenFile.Persons, ["subscriptions"] = CoOwned });

        var session = await StageAsync(client, app, app.AdultB,
            CoOwned.Replace("""{"person":"adult-1","share_bp":5000},{"person":"adult-2","share_bp":5000}""",
                """{"person":"adult-1","share_bp":3000},{"person":"adult-2","share_bp":7000}"""));
        var validated = await client.ValidateAsync(app.AdultB, session.Id, replace: true);
        Assert.True(validated.State == "validated", string.Join("; ", validated.Report!.Issues.Select(i => $"{i.Code} at {i.Path}")));
        Assert.True((await client.CommitRawAsync(app.AdultB, session.Id, replace: true)).IsSuccessStatusCode);

        Assert.Equal(1, (await client.GetAsync(app.AdultB, session.Id)).Report!.Sections["subscriptions"].Replaced);
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var item = await core.Items.AsNoTracking().Include(i => i.Owners).SingleAsync(i => i.DisplayName == "Shared");
        Assert.Equal(3000, item.Owners.Single(o => o.PersonId == app.AdultA.Value).ShareBp);
    }

    [Fact]
    public async Task A_replacement_that_makes_a_shared_item_private_away_from_another_adult_is_not_permitted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        const string shared = """[{"id":"sub-a","name":"A's","ownership":[{"person":"adult-1","share_bp":10000}],"auto_renew":true}]""";
        await CommitAsync(client, app, new Dictionary<string, string> { ["persons"] = GoldenFile.Persons, ["subscriptions"] = shared });
        var writesBefore = await WritesAsync(app);

        // A, its only owner, makes it private: B reads it today and would not any more.
        var session = await StageAsync(client, app, app.AdultA, shared.Replace("\"name\":\"A's\",", "\"name\":\"A's\",\"visibility\":\"private\","));
        var validated = await client.ValidateAsync(app.AdultA, session.Id, replace: true);

        Assert.Equal("invalid", validated.State);
        var issue = Assert.Single(validated.Report!.Issues, i => i.Severity == "error");
        Assert.Equal((ImportCodes.NotPermitted, "subscriptions[0].visibility"), (issue.Code, issue.Path));
        Assert.Equal(HttpStatusCode.Conflict, (await client.CommitRawAsync(app.AdultA, session.Id, replace: true)).StatusCode);
        Assert.Equal(writesBefore, await WritesAsync(app));
    }
    [Fact]
    public async Task A_replacement_reconciles_the_service_users_and_the_history_records_each_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, GoldenFile.Sections());
        await using var platformBefore = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var lastSeq = await platformBefore.AuditLog.MaxAsync(r => r.Seq);

        // sub-spotify, the only subscription with service_users: adult-2 in, adult-1 out.
        var swapped = GoldenFile.With("subscriptions", GoldenFile.Subscriptions.Replace(
            "\"service_users\":[\"adult-1\"]", "\"service_users\":[\"adult-2\"]"));
        var committed = await CommitAsync(client, app, swapped, replace: true);

        Assert.Equal(1, committed.Report!.Sections["subscriptions"].Replaced);
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        var spotify = await subs.Subscriptions.AsNoTracking().Include(s => s.Users).SingleAsync(s => s.ImportRef == "sub-spotify");
        Assert.Equal([app.AdultB.Value], spotify.Users.Select(u => u.PersonId).ToList());

        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var users = await platform.AuditLog
            .Where(r => r.Seq > lastSeq && r.Category == "write" && r.Entity == "subscriptions.subscription_user")
            .ToListAsync();
        Assert.Equal(["delete", "insert"], users.Select(r => r.Action).Order().ToList());
        Assert.Contains(app.AdultA.Value.ToString(), users.Single(r => r.Action == "delete").Data);
        Assert.Contains(app.AdultB.Value.ToString(), users.Single(r => r.Action == "insert").Data);
        Assert.All(users, r => Assert.Equal("import", r.Via));
        Assert.All(users, r => Assert.Equal([spotify.ItemId], r.ItemIds));
    }
}
