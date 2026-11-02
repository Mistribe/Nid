using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Platform.Contracts;
using Nid.Platform.Import;
using Nid.TestSupport;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Host.IntegrationTests.Import;

/// <summary>
/// A commit is all or nothing (import-format.md §11.4: "le serveur rejoue la validation complète"). A commit
/// timeout and a section that throws from ApplyAsync are already covered by ValidateCommitTests
/// (A_commit_out_of_time_rolls_back_and_leaves_the_session_validated,
/// A_section_that_throws_while_applying_rolls_back_and_leaves_the_session_validated); this file covers the
/// remaining ways a commit can fail to be all-or-nothing: a section reporting a failed Result (not a throw),
/// two commits racing the same session, and a conflict that appears only when commit re-validates.
/// </summary>
public class CommitRobustnessTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Last = "zz_last";

    private static object Adults(NidApp app) => new Dictionary<string, Guid> { ["adult-1"] = app.AdultA.Value, ["adult-2"] = app.AdultB.Value };

    private static Dictionary<string, string> GoldenWithNewDependentAndLast()
    {
        var sections = GoldenFile.With("persons",
            GoldenFile.Persons.TrimEnd().TrimEnd(']') + """,{"ref":"kid","kind":"dependent","display_name":"Léa"}]""");
        sections[Last] = """[{"id":"x"}]""";
        return sections;
    }

    [Fact]
    public async Task A_failure_in_the_last_section_takes_back_every_write_of_the_others()
    {
        await using var app = await NidApp.StartAsync(fixture, services => services.AddScoped<IImportSection, RefusingSection>());
        var client = new ImportClient(app);
        var validated = await client.RunAsync(app.AdultA, Adults(app), GoldenWithNewDependentAndLast());
        Assert.Equal("validated", validated.State);

        var response = await client.CommitRawAsync(app.AdultA, validated.Id);

        Assert.Equal(ImportCodes.NotCommitted, await ImportClient.CodeOf(response));
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
        {
            Assert.False(await core.Items.AnyAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.False(await core.Persons.AnyAsync(p => p.DisplayName == "Léa", cancellationToken: TestContext.Current.CancellationToken));
        }
        await using (var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString))
        {
            Assert.False(await subs.Subscriptions.AnyAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.False(await subs.Purchases.AnyAsync(cancellationToken: TestContext.Current.CancellationToken));
        }

        var after = await client.GetAsync(app.AdultA, validated.Id);
        Assert.Equal("validated", after.State);
        Assert.Contains(after.Report!.Issues, i => i.Code == ImportCodes.ApplyFailed && i.Message.Contains("test.refused"));
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        Assert.True(await platform.Batches.AnyAsync(b => b.SessionId == validated.Id, cancellationToken: TestContext.Current.CancellationToken)); // the staging is kept
    }

    [Fact]
    public async Task Of_two_commits_at_once_exactly_one_writes()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var validated = await client.RunAsync(app.AdultA, Adults(app), GoldenFile.Sections());

        var responses = await Task.WhenAll(client.CommitRawAsync(app.AdultA, validated.Id), client.CommitRawAsync(app.AdultA, validated.Id));

        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, responses.Select(r => r.StatusCode).Order().ToArray());
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        Assert.Equal(3, await subs.Subscriptions.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// B imports the same ids with other content between A's validate and A's commit. A's commit validates
    /// again, finds the conflict, and writes nothing.
    /// </summary>
    [Fact]
    public async Task A_commit_validates_again_and_refuses_what_became_a_conflict()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var mine = await client.RunAsync(app.AdultA, Adults(app), GoldenFile.Sections());
        Assert.Equal("validated", mine.State);

        var theirs = GoldenFile.With("subscriptions", GoldenFile.Subscriptions.Replace("Spotify Premium Duo", "Spotify de B"));
        var theirsValidated = await client.RunAsync(app.AdultB,
            new Dictionary<string, Guid> { ["adult-1"] = app.AdultB.Value, ["adult-2"] = app.AdultA.Value }, theirs);
        Assert.True((await client.CommitRawAsync(app.AdultB, theirsValidated.Id)).IsSuccessStatusCode);

        var response = await client.CommitRawAsync(app.AdultA, mine.Id);

        Assert.Equal(ImportCodes.NotCommitted, await ImportClient.CodeOf(response));
        var after = await client.GetAsync(app.AdultA, mine.Id);
        Assert.Equal("invalid", after.State);
        Assert.Contains(after.Report!.Issues, i => i.Code == ImportCodes.Conflict && i.Path == "subscriptions[0]");
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        Assert.Equal(3, await subs.Subscriptions.CountAsync(cancellationToken: TestContext.Current.CancellationToken)); // B's three, none of A's
    }

    /// <summary>Validates, then refuses to write: the injected failure of the first test.</summary>
    private sealed class RefusingSection : IImportSection
    {
        public Task<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>> ExportAsync(ExportContext context, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>>(new Dictionary<string, IReadOnlyList<JsonElement>>());

        public string Module => "test";
        public IReadOnlyList<string> Sections => [Last];
        public int Order => 100;

        public IReadOnlyList<ImportIssue> ValidateBatch(string section, JsonElement entities, int firstIndex) => [];

        public Task<SectionOutcome> ValidateAllAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
            => Task.FromResult(new SectionOutcome([], new Dictionary<string, SectionCounts>()));

        public Task<Result<IReadOnlyDictionary<string, SectionCounts>>> ApplyAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
            => Task.FromResult(Result.Failure<IReadOnlyDictionary<string, SectionCounts>>(new Failure("test.refused", "Refused on purpose.", null)));
    }
}
