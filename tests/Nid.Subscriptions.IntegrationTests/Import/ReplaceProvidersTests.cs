using System.Net;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests.Import;

/// <summary>1F3b: with <c>replace</c>, an import's own custom providers are replaced in place; an archived one stays a conflict.</summary>
public class ReplaceProvidersTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string SoloSpotify = """[{"key":"spotify","name":"Music Central","website":"https://www.spotify.com","label_keys":["other","streaming"]}]""";

    private static object Adults(NidApp app) => new Dictionary<string, Guid> { ["adult-1"] = app.AdultA.Value, ["adult-2"] = app.AdultB.Value };

    private static async Task<SessionResponse> StageAsync(ImportClient client, NidApp app, PersonId actor, IReadOnlyDictionary<string, string> sections)
    {
        var session = await client.StartAsync(actor, Adults(app));
        foreach (var (section, entities) in sections)
            Assert.True((await client.PutAsync(actor, session.Id, section, 0, entities)).Accepted);
        return session;
    }

    private static async Task<SessionResponse> CommitAsync(ImportClient client, NidApp app, IReadOnlyDictionary<string, string> sections, bool replace = false)
    {
        var session = await StageAsync(client, app, app.AdultA, sections);
        var validated = await client.ValidateAsync(app.AdultA, session.Id, replace);
        Assert.True(validated.State == "validated", string.Join("; ", validated.Report!.Issues.Select(i => $"{i.Code} at {i.Path}")));
        var committed = await client.CommitRawAsync(app.AdultA, session.Id, replace);
        Assert.True(committed.IsSuccessStatusCode, await committed.Content.ReadAsStringAsync());
        return await client.GetAsync(app.AdultA, session.Id);
    }

    /// <summary>The label the golden spotify provider does not hold: added by the replacement.</summary>
    private static async Task SeedLabelAsync(NidApp app, string key, string name)
    {
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        subs.Labels.Add(new LabelRow { Key = key, Name = name, Color = "#112233", Origin = "catalog" });
        await subs.SaveChangesAsync();
    }

    private static async Task ArchiveSpotifyAsync(NidApp app)
    {
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        await subs.Providers.Where(p => p.ImportRef == "spotify")
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ArchivedAt, (Instant?)SystemClock.Instance.GetCurrentInstant()));
    }

    [Fact]
    public async Task With_replace_my_custom_provider_takes_the_files_name_and_labels_under_the_same_key()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, GoldenFile.Sections());
        await SeedLabelAsync(app, "streaming", "Streaming");
        string keyBefore;
        string? hashBefore;
        await using (var before = PostgresFixture.NewSubscriptionsContext(app.ConnectionString))
        {
            var row = await before.Providers.AsNoTracking().SingleAsync(p => p.ImportRef == "spotify");
            (keyBefore, hashBefore) = (row.Key, row.ImportHash);
        }

        var changed = new Dictionary<string, string> { ["providers"] = SoloSpotify };
        var committed = await CommitAsync(client, app, changed, replace: true);

        Assert.Equal(new CountsResponse(0, 0, 0, 1), committed.Report!.Sections["providers"]);
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        var after = await subs.Providers.AsNoTracking().SingleAsync(p => p.ImportRef == "spotify");
        Assert.Equal(keyBefore, after.Key); // same row, same key
        Assert.Equal("Music Central", after.Name);
        Assert.Equal(new[] { "other", "streaming" }, after.LabelKeys);
        Assert.NotEqual(hashBefore, after.ImportHash);

        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var update = await platform.AuditLog.SingleAsync(
            r => r.Category == "write" && r.Action == "update" && r.Entity == "subscriptions.provider");
        Assert.Contains("Spotify", update.Data); // the old name, not "Music Central"
        Assert.Equal("import", update.Via);
    }

    [Fact]
    public async Task An_archived_provider_stays_a_conflict_even_with_replace()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, GoldenFile.Sections());
        await ArchiveSpotifyAsync(app);
        await SeedLabelAsync(app, "streaming", "Streaming");

        var changed = new Dictionary<string, string> { ["providers"] = SoloSpotify };
        var session = await StageAsync(client, app, app.AdultA, changed);
        var validated = await client.ValidateAsync(app.AdultA, session.Id, replace: true);

        Assert.Equal("invalid", validated.State);
        Assert.Equal("providers[0]", Assert.Single(validated.Report!.Issues, i => i.Code == ImportCodes.Conflict).Path);
        Assert.Equal(0, validated.Report.Sections["providers"].Replaced);
        Assert.Equal(HttpStatusCode.Conflict, (await client.CommitRawAsync(app.AdultA, session.Id, replace: true)).StatusCode);
    }

    [Fact]
    public async Task The_same_provider_file_after_a_replacement_is_unchanged()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, GoldenFile.Sections());
        await SeedLabelAsync(app, "streaming", "Streaming");
        var changed = new Dictionary<string, string> { ["providers"] = SoloSpotify };
        await CommitAsync(client, app, changed, replace: true);

        var again = await CommitAsync(client, app, changed, replace: true);

        Assert.Equal(new CountsResponse(0, 1, 0, 0), again.Report!.Sections["providers"]);
    }
}
