using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;
using Duration = NodaTime.Duration;

namespace Nid.Subscriptions.IntegrationTests.Catalog;

public class CatalogEndpointTests(PostgresFixture fixture, SharedNidApp shared) : IClassFixture<PostgresFixture>, IClassFixture<SharedNidApp>
{
    private const string Root = "/api/v1/subscriptions";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record LabelResponse(string Key, string Name, string Color);
    public sealed record ProviderResponse(string Key, string Name, string Origin, List<LabelResponse> Labels, string? Website, bool Archived);
    public sealed record ReportResponse(List<string> ProvidersAdded, List<string> LabelsCreated, List<string> AdoptedRemaining);
    public sealed record RunResponse(Guid Id, string Status, string Commit, string? FailureCode, ReportResponse? Report);
    public sealed record RunPageResponse(List<RunResponse> Items, int Total, int Page);
    public sealed record RunsResponse(string PinnedCommit, RunResponse? Latest, RunPageResponse Runs);

    private sealed class OkTrigger : ICatalogJobTrigger
    {
        public Task<Result> TriggerSyncAsync(CancellationToken ct) => Task.FromResult(Result.Success());
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("code").GetString();

    private static async Task SyncAsync(NidApp app)
    {
        app.Catalog.Serve(CatalogFixtures.RealLabels(), CatalogFixtures.RealProviders());
        var (_, failure) = await app.InvokeAsync(app.AdultA, new SyncCatalogCommand());
        Assert.Null(failure);
    }

    [Fact]
    public async Task Providers_are_listed_and_filtered_by_name()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SyncAsync(app);

        Assert.Equal(17, (await app.GetAsync<List<ProviderResponse>>(app.AdultB, $"{Root}/providers")).Count);

        var netflix = Assert.Single(await app.GetAsync<List<ProviderResponse>>(app.AdultB, $"{Root}/providers?q=NETF"));
        Assert.Equal(("netflix", "Netflix", "catalog", "https://www.netflix.com", false),
            (netflix.Key, netflix.Name, netflix.Origin, netflix.Website, netflix.Archived));
        Assert.Equal(["streaming", "vod"], netflix.Labels.Select(l => l.Key));
    }

    [Fact]
    public async Task A_custom_provider_is_created_with_201_and_its_generated_key()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Root}/providers",
            new { name = "Tinder", website = "https://tinder.com", labelKeys = new[] { CatalogLimits.OtherLabel } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ProviderResponse>(Json, cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Matches("^tinder-[a-z0-9]{6}$", created.Key);
        Assert.Contains(created.Key, (await app.GetAsync<List<ProviderResponse>>(app.AdultA, $"{Root}/providers")).Select(p => p.Key));
    }

    [Theory]
    [InlineData("""{"name":"","labelKeys":["other"]}""")]
    [InlineData("""{"name":"Tinder","website":"ftp://tinder.com","labelKeys":["other"]}""")]
    [InlineData("""{"name":"Tinder","labelKeys":["nope"]}""")]
    [InlineData("""{"name":"Tinder"}""")]
    public async Task An_invalid_provider_is_422(string body)
    {
        var app = shared.App;
        var response = await app.RawTextAsync(app.AdultA, HttpMethod.Post, $"{Root}/providers", body);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, CatalogCodes.InvalidProvider), (response.StatusCode, await CodeOf(response)));
    }

    /// <summary>An explicit null is the same as an absent field: CreateProviderCommand already refuses no label (422).</summary>
    [Fact]
    public async Task LabelKeys_explicitly_null_is_the_same_as_absent()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var response = await app.RawTextAsync(app.AdultA, HttpMethod.Post, $"{Root}/providers", """{"name":"Tinder","labelKeys":null}""");
        Assert.Equal((HttpStatusCode.UnprocessableEntity, CatalogCodes.InvalidProvider), (response.StatusCode, await CodeOf(response)));
    }

    /// <summary>
    /// A null entry inside the array must never reach the store as a null element of a text[] column (a 500):
    /// it is refused as malformed input before the command runs (Task 6 review).
    /// </summary>
    [Fact]
    public async Task A_null_label_key_inside_the_array_is_refused_not_crashed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var response = await app.RawTextAsync(app.AdultA, HttpMethod.Post, $"{Root}/providers", """{"name":"Tinder","labelKeys":["other",null]}""");
        Assert.Equal((HttpStatusCode.BadRequest, "request.malformed"), (response.StatusCode, await CodeOf(response)));
    }

    /// <summary>The search parameter gets a sane maximum length: at most a provider name (Task 6 review).</summary>
    [Fact]
    public async Task The_providers_search_query_has_a_maximum_length()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var tooLong = new string('a', CatalogLimits.MaxNameLength + 1);
        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Root}/providers?q={tooLong}");
        Assert.Equal((HttpStatusCode.BadRequest, "request.malformed"), (response.StatusCode, await CodeOf(response)));
    }

    [Fact]
    public async Task Labels_are_listed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SyncAsync(app);

        var labels = await app.GetAsync<List<LabelResponse>>(app.AdultB, $"{Root}/labels");

        Assert.Contains(new LabelResponse(CatalogLimits.OtherLabel, "Other", CatalogLimits.NeutralColor), labels);
        Assert.Contains(new LabelResponse("home", "Home", CatalogLimits.NeutralColor), labels);
        Assert.Equal(20, labels.Count); // the 19 of the catalog reading, plus the local one
    }

    [Fact]
    public async Task Runs_come_with_the_pinned_commit_and_their_report()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SyncAsync(app);

        var runs = await app.GetAsync<RunsResponse>(app.AdultA, $"{Root}/catalog/runs");

        Assert.Equal(CatalogFixtures.PinnedCommit, runs.PinnedCommit);
        var run = Assert.Single(runs.Runs.Items);
        Assert.Equal(run.Id, runs.Latest!.Id);
        Assert.Equal(("succeeded", CatalogFixtures.PinnedCommit, (string?)null), (run.Status, run.Commit, run.FailureCode));
        Assert.Equal(17, run.Report!.ProvidersAdded.Count);
        Assert.Equal(["home"], run.Report.LabelsCreated);
    }

    private static async Task SeedRunsAsync(NidApp app)
    {
        await using var seed = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        var start = Instant.FromUtc(2025, 10, 1, 7, 0);
        for (var i = 1; i <= 51; i++)
            seed.CatalogSyncRuns.Add(new CatalogSyncRunRow
            {
                Id = Guid.CreateVersion7(), StartedAt = start + Duration.FromMinutes(i), FinishedAt = start + Duration.FromMinutes(i),
                Status = "succeeded", Commit = $"c{i}",
            });
        await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Runs_answer_one_page_and_the_total_and_every_page_keeps_the_newest_run()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SeedRunsAsync(app);

        var first = await app.GetAsync<RunsResponse>(app.AdultA, $"{Root}/catalog/runs");
        var second = await app.GetAsync<RunsResponse>(app.AdultA, $"{Root}/catalog/runs?page=2");

        Assert.Equal((50, 51, 1), (first.Runs.Items.Count, first.Runs.Total, first.Runs.Page));
        Assert.Equal((1, 51, 2), (second.Runs.Items.Count, second.Runs.Total, second.Runs.Page));
        Assert.Equal("c1", second.Runs.Items[0].Commit);
        // Page 1 still lists the newest run, and page 2 carries it in the card.
        Assert.Equal(first.Runs.Items[0].Id, first.Latest!.Id);
        Assert.Equal(first.Latest.Id, second.Latest!.Id);
    }

    [Fact]
    public async Task Runs_page_is_clamped()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SeedRunsAsync(app);

        Assert.Equal(1, (await app.GetAsync<RunsResponse>(app.AdultA, $"{Root}/catalog/runs?page=0")).Runs.Page);
        var past = await app.GetAsync<RunsResponse>(app.AdultA, $"{Root}/catalog/runs?page={int.MaxValue}");
        Assert.Equal((0, 51), (past.Runs.Items.Count, past.Runs.Total));
        Assert.NotNull(past.Latest);
    }

    [Fact]
    public async Task Runs_without_any_run_have_no_latest()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var runs = await app.GetAsync<RunsResponse>(app.AdultA, $"{Root}/catalog/runs");

        Assert.Null(runs.Latest);
        Assert.Equal((0, 0), (runs.Runs.Items.Count, runs.Runs.Total));
    }

    [Fact]
    public async Task Sync_is_202_then_409_while_a_sync_runs()
    {
        await using var app = await NidApp.StartAsync(fixture, services => services.AddSingleton<ICatalogJobTrigger>(new OkTrigger()));

        var accepted = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Root}/catalog/sync");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.True((await accepted.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken: TestContext.Current.CancellationToken)).GetProperty("requested").GetBoolean());

        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ICatalogRunLog>().TryStartAsync(CatalogFixtures.PinnedCommit, TestContext.Current.CancellationToken);
        var busy = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Root}/catalog/sync");
        Assert.Equal((HttpStatusCode.Conflict, CatalogCodes.SyncRunning), (busy.StatusCode, await CodeOf(busy)));
    }

    /// <summary>
    /// Final review F1: a run a crash left running blocks the on-demand sync for an hour, not for ever — after a success
    /// the startup no longer syncs and the catalog has no schedule, so nothing else would ever close it.
    /// </summary>
    [Theory]
    [InlineData(120, HttpStatusCode.Accepted)]
    [InlineData(5, HttpStatusCode.Conflict)]
    public async Task A_run_left_running_blocks_the_sync_for_an_hour_only(int startedMinutesAgo, HttpStatusCode expected)
    {
        await using var app = await NidApp.StartAsync(fixture, services => services.AddSingleton<ICatalogJobTrigger>(new OkTrigger()));
        await SyncAsync(app);
        var now = app.Services.GetRequiredService<IClock>().GetCurrentInstant();
        var stale = Guid.CreateVersion7();
        await using (var seed = PostgresFixture.NewSubscriptionsContext(app.ConnectionString))
        {
            seed.CatalogSyncRuns.Add(new CatalogSyncRunRow
            {
                Id = stale, StartedAt = now - Duration.FromMinutes(startedMinutesAgo), Status = "running", Commit = CatalogFixtures.PinnedCommit,
            });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Root}/catalog/sync");

        Assert.Equal(expected, response.StatusCode);
        await using var check = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        var row = await check.CatalogSyncRuns.AsNoTracking().SingleAsync(r => r.Id == stale, cancellationToken: TestContext.Current.CancellationToken);
        if (expected == HttpStatusCode.Accepted)
            Assert.Equal(("failed", CatalogCodes.RunAbandoned, (Instant?)now), (row.Status, row.FailureCode, row.FinishedAt));
        else
            Assert.Equal(("running", CatalogCodes.SyncRunning), (row.Status, await CodeOf(response)));
    }

    [Fact]
    public async Task Without_jobs_a_sync_is_refused_as_such()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Root}/catalog/sync");
        Assert.Equal((HttpStatusCode.UnprocessableEntity, CatalogCodes.JobsDisabled), (response.StatusCode, await CodeOf(response)));
    }

    /// <summary>Spec §6: the journal and the sync are the admin's; B, who is not admin, gets 403 platform.admin_required.</summary>
    [Fact]
    public async Task The_journal_and_the_sync_are_refused_to_an_adult_who_is_not_admin()
    {
        await using var app = await NidApp.StartAsync(fixture, services => services.AddSingleton<ICatalogJobTrigger>(new OkTrigger()));

        var runs = await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Root}/catalog/runs");
        var sync = await app.RawAsync(app.AdultB, HttpMethod.Post, $"{Root}/catalog/sync");

        Assert.Equal((HttpStatusCode.Forbidden, AdminCodes.Required), (runs.StatusCode, await CodeOf(runs)));
        Assert.Equal((HttpStatusCode.Forbidden, AdminCodes.Required), (sync.StatusCode, await CodeOf(sync)));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Root}/providers")).StatusCode); // the lists stay open
    }

    [Theory]
    [InlineData("/providers")]
    [InlineData("/labels")]
    [InlineData("/catalog/runs")]
    public async Task Every_catalog_endpoint_requires_a_session(string path)
    {
        var app = shared.App;
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.RawAnonymousAsync(HttpMethod.Get, Root + path)).StatusCode);
    }
}
