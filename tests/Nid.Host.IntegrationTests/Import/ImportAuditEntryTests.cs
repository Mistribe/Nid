using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Import;

public class ImportAuditEntryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static object Adults(NidApp app) => new Dictionary<string, Guid> { ["adult-1"] = app.AdultA.Value, ["adult-2"] = app.AdultB.Value };

    /// <summary>The batch hashes as the client computed them, in the commit order (§11.4), each batch index 0.</summary>
    private static string ExpectedHash(IEnumerable<string> batchHashesInCommitOrder)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", batchHashesInCommitOrder))));

    [Fact]
    public async Task A_commit_leaves_exactly_one_import_entry_with_its_hash_format_replace_and_counts()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var validated = await client.RunAsync(app.AdultA, Adults(app), GoldenFile.Sections());
        Assert.True((await client.CommitRawAsync(app.AdultA, validated.Id)).IsSuccessStatusCode);

        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var entry = Assert.Single(await platform.AuditLog.Where(r => r.Category == "import").ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(("commit", "import_session", validated.Id.ToString(), "person", app.AdultA.Value, "import"),
            (entry.Action, entry.Entity, entry.EntityKey, entry.ActorKind, entry.PersonId!.Value, entry.Via));
        Assert.Empty(entry.ItemIds);

        using var data = JsonDocument.Parse(entry.Data);
        var root = data.RootElement;
        Assert.Equal(validated.Id.ToString(), root.GetProperty("session").GetString());
        Assert.Equal("nid.data", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.False(root.GetProperty("replace").GetBoolean());
        // GoldenFile.Subscriptions holds three subscriptions (Spotify, NordVPN, WoW), all new on a first import.
        Assert.Equal(3, root.GetProperty("counts").GetProperty("subscriptions").GetProperty("created").GetInt32());
        Assert.Equal(ExpectedHash(await client.BatchHashesInCommitOrderAsync(app.AdultA, validated.Id)),
            root.GetProperty("content_hash").GetString());
        Assert.DoesNotContain("Spotify", entry.Data); // no value of the file
    }

    [Fact]
    public async Task A_refused_commit_leaves_no_import_entry()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var first = await client.RunAsync(app.AdultA, Adults(app), GoldenFile.Sections());
        Assert.True((await client.CommitRawAsync(app.AdultA, first.Id)).IsSuccessStatusCode);
        var changed = GoldenFile.With("subscriptions", GoldenFile.Subscriptions.Replace("Spotify Premium Duo", "Spotify Family"));
        var second = await client.RunAsync(app.AdultA, Adults(app), changed);
        Assert.Equal(HttpStatusCode.Conflict, (await client.CommitRawAsync(app.AdultA, second.Id)).StatusCode);

        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        Assert.Equal(1, await platform.AuditLog.CountAsync(r => r.Category == "import", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_importer_reads_the_entry_on_the_session_and_no_one_else_reads_the_session()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var validated = await client.RunAsync(app.AdultA, Adults(app), GoldenFile.Sections());
        Assert.Null(validated.Audit); // not committed yet
        Assert.True((await client.CommitRawAsync(app.AdultA, validated.Id)).IsSuccessStatusCode);

        var session = await client.GetAsync(app.AdultA, validated.Id);
        Assert.NotNull(session.Audit);
        Assert.Equal(("nid.data", 1, false), (session.Audit!.Format, session.Audit.Version, session.Audit.Replace));
        Assert.Equal(64, session.Audit.ContentHash.Length);
        Assert.Equal(HttpStatusCode.NotFound,
            (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{ImportClient.Root}/{validated.Id}")).StatusCode);
    }

    [Fact]
    public async Task The_admin_journal_does_not_show_imports()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var validated = await client.RunAsync(app.AdultA, Adults(app), GoldenFile.Sections());
        Assert.True((await client.CommitRawAsync(app.AdultA, validated.Id)).IsSuccessStatusCode);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/admin/security-journal"); // AdultA is admin
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // an error body would not contain it either
        Assert.DoesNotContain("import_session", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
