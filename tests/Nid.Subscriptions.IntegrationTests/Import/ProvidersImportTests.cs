using System.Net;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Infrastructure.Persistence;
using Nid.Subscriptions.IntegrationTests.Catalog;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Import;

[Collection("postgres")]
public class ProvidersImportTests(PostgresFixture fixture)
{
    private const string Owner = "\"ownership\":[{\"person\":\"adult-1\",\"share_bp\":10000}]";

    private static object Adults(NidApp app) => new Dictionary<string, Guid> { ["adult-1"] = app.AdultA.Value, ["adult-2"] = app.AdultB.Value };

    private static string Append(string golden, string entities) => golden.TrimEnd().TrimEnd(']') + "," + entities.TrimStart().TrimStart('[');

    private static async Task<SessionResponse> CommitAsync(ImportClient client, NidApp app, PersonId actor, IReadOnlyDictionary<string, string> sections)
    {
        var validated = await client.RunAsync(actor, Adults(app), sections);
        Assert.True(validated.State == "validated", string.Join("; ", validated.Report!.Issues.Select(i => $"{i.Code} at {i.Path}")));
        var committed = await client.CommitRawAsync(actor, validated.Id);
        Assert.True(committed.IsSuccessStatusCode, await committed.Content.ReadAsStringAsync());
        return await client.GetAsync(actor, validated.Id);
    }

    private static async Task<List<ProviderRow>> CustomProvidersAsync(NidApp app)
    {
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        return await subs.Providers.AsNoTracking().Where(p => p.Origin == "custom").ToListAsync();
    }

    private static async Task<string?> ProviderKeyOfAsync(NidApp app, string subscriptionRef)
    {
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        return (await subs.Subscriptions.AsNoTracking().SingleAsync(s => s.ImportRef == subscriptionRef)).ProviderKey;
    }

    [Fact]
    public async Task The_providers_of_the_file_are_created_at_commit_and_its_references_mapped()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var committed = await CommitAsync(new ImportClient(app), app, app.AdultA, GoldenFile.Sections());

        Assert.Equal(new CountsResponse(3, 0, 0), committed.Report!.Sections["providers"]);
        var created = await CustomProvidersAsync(app);
        Assert.Equal(["nordvpn", "spotify", "wow"], created.Select(p => p.ImportRef!).Order(StringComparer.Ordinal));
        Assert.All(created, p => Assert.Equal(app.AdultA.Value, p.CreatedBy));
        var spotify = Assert.Single(created, p => p.ImportRef == "spotify");
        Assert.Matches("^spotify-[a-z0-9]{6}$", spotify.Key);
        Assert.Equal(("Spotify", "https://www.spotify.com"), (spotify.Name, spotify.Website));
        Assert.Equal(spotify.Key, await ProviderKeyOfAsync(app, "sub-spotify"));
    }

    [Fact]
    public async Task Importing_the_same_file_again_creates_no_provider()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, GoldenFile.Sections());

        var again = await CommitAsync(client, app, app.AdultA, GoldenFile.Sections());

        Assert.Equal(new CountsResponse(0, 3, 0), again.Report!.Sections["providers"]);
        Assert.Equal(3, (await CustomProvidersAsync(app)).Count);
    }

    /// <summary>
    /// Final review H4: an entry is resolved to an earlier import's provider only while it can still be linked — visible
    /// and not archived, the rule every write applies. The importer's own archived provider keeps its reference, so its
    /// entry is a conflict; another adult's archived one is left alone and the entry gets a new provider.
    /// </summary>
    [Fact]
    public async Task An_archived_provider_of_an_earlier_import_is_not_reused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, GoldenFile.Sections());
        await using (var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString))
            await subs.Providers.Where(p => p.ImportRef == "wow")
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ArchivedAt, (NodaTime.Instant?)NodaTime.SystemClock.Instance.GetCurrentInstant()));

        var mine = await client.RunAsync(app.AdultA, Adults(app), GoldenFile.Sections());

        Assert.Equal(new CountsResponse(0, 2, 1), mine.Report!.Sections["providers"]);
        Assert.Equal("providers[2]", Assert.Single(mine.Report.Issues, i => i.Code == ImportCodes.Conflict).Path);
        Assert.True((await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{ImportClient.Root}/{mine.Id}")).IsSuccessStatusCode);

        var theirs = await CommitAsync(client, app, app.AdultB, GoldenFile.Sections());

        Assert.Equal(new CountsResponse(1, 2, 0), theirs.Report!.Sections["providers"]);
        Assert.Equal("wow", Assert.Single(await CustomProvidersAsync(app), p => p.CreatedBy == app.AdultB.Value).ImportRef);
    }

    [Fact]
    public async Task The_same_reference_with_other_content_is_a_conflict_that_blocks_the_commit()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, GoldenFile.Sections());

        var changed = GoldenFile.With("providers", GoldenFile.Providers.Replace("World of Warcraft", "WoW"));
        var validated = await client.RunAsync(app.AdultA, Adults(app), changed);

        Assert.Equal("invalid", validated.State);
        Assert.Equal(new CountsResponse(0, 2, 1), validated.Report!.Sections["providers"]);
        Assert.Equal("providers[2]", Assert.Single(validated.Report.Issues, i => i.Code == ImportCodes.Conflict).Path);
        Assert.Equal(HttpStatusCode.Conflict, (await client.CommitRawAsync(app.AdultA, validated.Id)).StatusCode);
    }

    [Fact]
    public async Task An_unknown_provider_key_blocks_at_its_path()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var file = GoldenFile.With("subscriptions",
            Append(GoldenFile.Subscriptions, $$"""[{"id":"sub-x","provider_key":"nope","name":"X",{{Owner}},"auto_renew":true}]"""));

        var validated = await new ImportClient(app).RunAsync(app.AdultA, Adults(app), file);

        var issue = Assert.Single(validated.Report!.Issues, i => i.Severity == "error");
        Assert.Equal((ImportCodes.UnresolvedReference, "subscriptions[3].provider_key"), (issue.Code, issue.Path));
    }

    [Fact]
    public async Task A_catalog_provider_is_named_without_a_providers_entry()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedProviderAsync("netflix", "Netflix", ProviderOrigin.Catalog);

        await CommitAsync(new ImportClient(app), app, app.AdultA, new Dictionary<string, string>
        {
            ["persons"] = GoldenFile.Persons,
            ["subscriptions"] = $$"""[{"id":"sub-n","provider_key":"netflix","name":"Netflix",{{Owner}},"auto_renew":true}]""",
        });

        Assert.Equal("netflix", await ProviderKeyOfAsync(app, "sub-n"));
        Assert.Empty(await CustomProvidersAsync(app));
    }

    [Fact]
    public async Task An_archived_provider_cannot_be_named_by_a_new_subscription()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedProviderAsync("hulu", "Hulu", ProviderOrigin.Catalog, archived: true);

        var validated = await new ImportClient(app).RunAsync(app.AdultA, Adults(app), new Dictionary<string, string>
        {
            ["persons"] = GoldenFile.Persons,
            ["subscriptions"] = $$"""[{"id":"sub-h","provider_key":"hulu","name":"Hulu",{{Owner}},"auto_renew":true}]""",
        });

        Assert.Equal((ImportCodes.UnresolvedReference, "subscriptions[0].provider_key"),
            (Assert.Single(validated.Report!.Issues).Code, validated.Report.Issues[0].Path));
    }

    /// <summary>Design §3.5 on the import path: B's file cannot name a provider only A's private subscription uses.</summary>
    [Fact]
    public async Task A_provider_only_a_hidden_subscription_uses_is_unknown_to_the_other_adults_import()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await ProviderVisibilityTests.CreateCustomAsync(app, app.AdultA, "Tinder");
        await app.LinkProviderAsync(await app.NewSubscriptionAsync("Tinder Gold", app.AdultA, Visibility.Private), hidden);

        var validated = await new ImportClient(app).RunAsync(app.AdultB, Adults(app), new Dictionary<string, string>
        {
            ["persons"] = GoldenFile.Persons,
            ["subscriptions"] = $$"""[{"id":"sub-b","provider_key":"{{hidden}}","name":"B's","ownership":[{"person":"adult-2","share_bp":10000}],"auto_renew":true}]""",
        });

        var issue = Assert.Single(validated.Report!.Issues);
        Assert.Equal((ImportCodes.UnresolvedReference, "subscriptions[0].provider_key"), (issue.Code, issue.Path));
    }

    /// <summary>Review Focus 3: B imports the very file A committed. Nothing is created, nothing is a conflict.</summary>
    [Fact]
    public async Task The_same_file_imported_by_the_other_adult_creates_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, GoldenFile.Sections());

        var again = await CommitAsync(client, app, app.AdultB, GoldenFile.Sections());

        Assert.Equal(new CountsResponse(0, 3, 0), again.Report!.Sections["providers"]);
        Assert.Equal(new CountsResponse(0, 3, 0), again.Report.Sections["subscriptions"]);
        Assert.All(await CustomProvidersAsync(app), p => Assert.Equal(app.AdultA.Value, p.CreatedBy));
        Assert.Equal(3, (await CustomProvidersAsync(app)).Count);
    }

    /// <summary>Review Focus 3: a file key is local to its file. B's own "wow" is B's provider, never a conflict with A's.</summary>
    [Fact]
    public async Task Another_file_of_the_other_adult_reusing_a_key_gets_its_own_provider()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, GoldenFile.Sections());

        var mine = await CommitAsync(client, app, app.AdultB, new Dictionary<string, string>
        {
            ["persons"] = GoldenFile.Persons,
            ["providers"] = """[{"key":"wow","name":"WoW Classic","label_keys":["other"]}]""",
            ["subscriptions"] = """[{"id":"sub-b-wow","provider_key":"wow","name":"WoW Classic","ownership":[{"person":"adult-2","share_bp":10000}],"auto_renew":true}]""",
        });

        Assert.Equal(new CountsResponse(1, 0, 0), mine.Report!.Sections["providers"]);
        var providers = await CustomProvidersAsync(app);
        var bs = Assert.Single(providers, p => p.CreatedBy == app.AdultB.Value);
        Assert.Equal(("wow", "WoW Classic"), (bs.ImportRef, bs.Name));
        Assert.Equal(bs.Key, await ProviderKeyOfAsync(app, "sub-b-wow"));
        Assert.NotEqual(Assert.Single(providers, p => p.CreatedBy == app.AdultA.Value && p.ImportRef == "wow").Key, bs.Key);
    }

    /// <summary>
    /// Review Focus 3: B's import answers the same for a key only A's private subscription uses as for a key that never
    /// existed — same code, same path, same message — and B's providers entry of that key is B's own new provider.
    /// </summary>
    [Fact]
    public async Task A_hidden_provider_key_answers_exactly_like_an_unknown_key()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await ProviderVisibilityTests.CreateCustomAsync(app, app.AdultA, "Tinder");
        await app.LinkProviderAsync(await app.NewSubscriptionAsync("Tinder Gold", app.AdultA, Visibility.Private), hidden);
        var client = new ImportClient(app);

        async Task<IssueResponse> IssueFor(string key)
        {
            var validated = await client.RunAsync(app.AdultB, Adults(app), new Dictionary<string, string>
            {
                ["persons"] = GoldenFile.Persons,
                ["subscriptions"] = $$"""[{"id":"sub-b","provider_key":"{{key}}","name":"B's","ownership":[{"person":"adult-2","share_bp":10000}],"auto_renew":true}]""",
            });
            Assert.True((await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{ImportClient.Root}/{validated.Id}")).IsSuccessStatusCode);
            return Assert.Single(validated.Report!.Issues);
        }

        var unknown = "tinder-zzzzzz";
        var forHidden = await IssueFor(hidden);
        var forUnknown = await IssueFor(unknown);

        Assert.Equal((forUnknown.Code, forUnknown.Path, forUnknown.Severity), (forHidden.Code, forHidden.Path, forHidden.Severity));
        Assert.Equal(forUnknown.Message.Replace(unknown, "?"), forHidden.Message.Replace(hidden, "?"));

        // A file entry under the hidden provider's very key is a reference local to B's file: B gets a new provider.
        var mine = await CommitAsync(client, app, app.AdultB, new Dictionary<string, string>
        {
            ["persons"] = GoldenFile.Persons,
            ["providers"] = $$"""[{"key":"{{hidden}}","name":"Tinder","label_keys":["other"]}]""",
            ["subscriptions"] = $$"""[{"id":"sub-b","provider_key":"{{hidden}}","name":"B's","ownership":[{"person":"adult-2","share_bp":10000}],"auto_renew":true}]""",
        });
        Assert.Equal(new CountsResponse(1, 0, 0), mine.Report!.Sections["providers"]);
        var stored = await ProviderKeyOfAsync(app, "sub-b");
        Assert.NotEqual(hidden, stored);
        Assert.Equal(app.AdultB.Value, Assert.Single(await CustomProvidersAsync(app), p => p.Key == stored).CreatedBy);
    }

    /// <summary>
    /// Review Focus 3: A imported a provider only A's private subscription uses. B importing the very same entry never
    /// matches it (that would reveal it): B's import creates B's own, exactly as if A's did not exist.
    /// </summary>
    [Fact]
    public async Task The_same_entry_imported_by_the_other_adult_never_matches_a_hidden_provider()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        const string entry = """[{"key":"tinder","name":"Tinder","label_keys":["other"]}]""";
        await CommitAsync(client, app, app.AdultA, new Dictionary<string, string>
        {
            ["persons"] = GoldenFile.Persons,
            ["providers"] = entry,
            ["subscriptions"] = $$"""[{"id":"sub-a-tinder","provider_key":"tinder","name":"Tinder Gold","visibility":"private",{{Owner}},"auto_renew":true}]""",
        });
        var asHidden = Assert.Single(await CustomProvidersAsync(app));

        var validated = await client.RunAsync(app.AdultB, Adults(app), new Dictionary<string, string>
        {
            ["persons"] = GoldenFile.Persons,
            ["providers"] = entry,
            ["subscriptions"] = """[{"id":"sub-b-tinder","provider_key":"tinder","name":"Tinder","ownership":[{"person":"adult-2","share_bp":10000}],"auto_renew":true}]""",
        });
        Assert.Equal("validated", validated.State);
        Assert.Equal(new CountsResponse(1, 0, 0), validated.Report!.Sections["providers"]);
        Assert.DoesNotContain(validated.Report.Issues, i => i.Path.StartsWith("providers", StringComparison.Ordinal) || i.Severity == "error");
        Assert.True((await client.CommitRawAsync(app.AdultB, validated.Id)).IsSuccessStatusCode);

        var providers = await CustomProvidersAsync(app);
        Assert.Equal(2, providers.Count);
        var bs = Assert.Single(providers, p => p.CreatedBy == app.AdultB.Value);
        Assert.NotEqual(asHidden.Key, bs.Key);
        Assert.Equal(bs.Key, await ProviderKeyOfAsync(app, "sub-b-tinder"));
    }

    public sealed record BlockingCase(string Entities, string Code, string Path, bool OnReceipt);

    private static readonly Dictionary<string, BlockingCase> Cases = new()
    {
        ["bad key"] = new("""[{"key":"Spotify","name":"Spotify","label_keys":["other"]}]""", ImportCodes.InvalidId, "providers[0].key", true),
        ["empty name"] = new("""[{"key":"spotify","name":" ","label_keys":["other"]}]""", ImportCodes.InvalidValue, "providers[0].name", true),
        ["ftp website"] = new("""[{"key":"spotify","name":"Spotify","website":"ftp://spotify.com","label_keys":["other"]}]""", ImportCodes.InvalidValue, "providers[0].website", true),
        ["no label"] = new("""[{"key":"spotify","name":"Spotify","label_keys":[]}]""", ImportCodes.InvalidValue, "providers[0].label_keys", true),
        ["null label"] = new("""[{"key":"spotify","name":"Spotify","label_keys":[null]}]""", ImportCodes.MalformedEntity, "providers[0].label_keys[0]", true),
        ["unknown member"] = new("""[{"key":"spotify","name":"Spotify","label_keys":["other"],"icon":"x"}]""", ImportCodes.MalformedEntity, "providers[0]", true),
        ["unknown label"] = new(Append(GoldenFile.Providers, """[{"key":"extra","name":"Extra","label_keys":["nope"]}]"""), ImportCodes.UnresolvedReference, "providers[3].label_keys[0]", false),
        ["duplicate key"] = new(Append(GoldenFile.Providers, """[{"key":"spotify","name":"Again","label_keys":["other"]}]"""), ImportCodes.DuplicateId, "providers[3].key", false),
    };

    public static TheoryData<string> CaseNames() => new(Cases.Keys);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Every_blocking_error_of_a_provider_is_reported_where_it_is(string name)
    {
        var @case = Cases[name];
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);

        IssueResponse issue;
        if (@case.OnReceipt)
        {
            var session = await client.StartAsync(app.AdultA, Adults(app));
            var batch = await client.PutAsync(app.AdultA, session.Id, "providers", 0, @case.Entities);
            Assert.False(batch.Accepted);
            issue = Assert.Single(batch.Issues, i => i.Severity == "error");
        }
        else
        {
            var validated = await client.RunAsync(app.AdultA, Adults(app), GoldenFile.With("providers", @case.Entities));
            Assert.Equal("invalid", validated.State);
            issue = Assert.Single(validated.Report!.Issues, i => i.Severity == "error");
        }

        Assert.Equal(@case.Code, issue.Code);
        if (@case.Code == ImportCodes.MalformedEntity && !@case.Path.Contains('.'))
            Assert.StartsWith(@case.Path, issue.Path); // the member comes from System.Text.Json's own path
        else
            Assert.Equal(@case.Path, issue.Path);
    }
}
