using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;
using Duration = NodaTime.Duration;

namespace Nid.Subscriptions.IntegrationTests.Catalog;

[Collection("postgres")]
public class CatalogRequestTests(PostgresFixture fixture)
{
    private sealed class RecordingTrigger : ICatalogJobTrigger
    {
        public int Calls;

        public Task<Result> TriggerSyncAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Result.Success());
        }
    }

    [Fact]
    public async Task A_sync_command_runs_offline_in_tests_and_journals_its_failure()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var (run, failure) = await app.InvokeAsync(app.AdultA, new SyncCatalogCommand());

        Assert.Null(failure);
        var view = (CatalogSyncRunView)run!;
        Assert.Equal(("failed", CatalogCodes.SourceUnavailable, CatalogFixtures.PinnedCommit), (view.Status, view.FailureCode, view.Commit));
        Assert.NotNull(view.FailureMessage);
    }

    [Fact]
    public async Task A_successful_sync_is_answered_with_its_report()
    {
        await using var app = await NidApp.StartAsync(fixture);
        app.Catalog.Serve(CatalogFixtures.RealLabels(), CatalogFixtures.RealProviders());

        var (run, _) = await app.InvokeAsync(app.AdultA, new SyncCatalogCommand());

        var view = (CatalogSyncRunView)run!;
        Assert.Equal("succeeded", view.Status);
        Assert.Equal(17, view.Report!.ProvidersAdded.Count);
        Assert.NotNull(view.FinishedAt);
    }

    [Fact]
    public async Task Without_jobs_a_sync_request_says_so()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (_, failure) = await app.InvokeAsync(app.AdultA, new RequestCatalogSyncCommand());
        Assert.Equal(CatalogCodes.JobsDisabled, failure?.Code);
    }

    [Fact]
    public async Task A_sync_request_triggers_the_job_unless_a_sync_is_running()
    {
        var trigger = new RecordingTrigger();
        await using var app = await NidApp.StartAsync(fixture, services => services.AddSingleton<ICatalogJobTrigger>(trigger));

        var (requested, failure) = await app.InvokeAsync(app.AdultA, new RequestCatalogSyncCommand());
        Assert.Null(failure);
        Assert.Equal(new CatalogSyncRequested(true), requested);
        Assert.Equal(1, trigger.Calls);

        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ICatalogRunLog>().TryStartAsync(CatalogFixtures.PinnedCommit, default);

        var (_, running) = await app.InvokeAsync(app.AdultA, new RequestCatalogSyncCommand());
        Assert.Equal(CatalogCodes.SyncRunning, running?.Code);
        Assert.Equal(1, trigger.Calls);
    }

    /// <summary>Spec §6 (amended after 1G): the sync journal and the sync itself are the admin's. B is not admin.</summary>
    [Fact]
    public async Task The_journal_requests_are_refused_to_an_adult_who_is_not_admin()
    {
        await using var app = await NidApp.StartAsync(fixture);

        foreach (var request in new object[] { new SyncCatalogCommand(), new RequestCatalogSyncCommand(), new GetCatalogRunsQuery() })
        {
            var (_, failure) = await app.InvokeAsync(app.AdultB, request);
            Assert.Equal(AdminCodes.Required, failure?.Code);
        }

        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        Assert.False(await subs.CatalogSyncRuns.AnyAsync()); // refused before the handler: no run journaled
    }

    /// <summary>
    /// Spec §3.6 (amended after 1G): A is admin, and still reads nothing of B's private subscriptions — not the adopted
    /// key one of them uses, not the one the catalog replaced, not a custom conflict only B's private subscription uses,
    /// and no count of what was dropped. A's own adopted keys, shared or private, stay listed; the stored report is whole.
    /// </summary>
    [Fact]
    public async Task The_journal_names_no_adopted_or_custom_provider_the_reader_cannot_see()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var links = new (string Key, ProviderOrigin Origin, PersonId Owner, Visibility Visibility)[]
        {
            ("a-visible", ProviderOrigin.Adopted, app.AdultA, Visibility.Shared),
            ("a-private", ProviderOrigin.Adopted, app.AdultA, Visibility.Private),
            ("a-replaced", ProviderOrigin.Adopted, app.AdultA, Visibility.Shared),
            ("b-secret", ProviderOrigin.Adopted, app.AdultB, Visibility.Private),
            ("b-replaced", ProviderOrigin.Adopted, app.AdultB, Visibility.Private),
            ("tinder-k3f9qa", ProviderOrigin.Custom, app.AdultB, Visibility.Private),
        };
        foreach (var (key, origin, owner, visibility) in links)
        {
            await app.SeedProviderAsync(key, key, origin, createdBy: owner);
            await app.LinkProviderAsync(await app.NewSubscriptionAsync("uses " + key, owner, visibility), key);
        }

        app.Catalog.Serve(
            Encoding.UTF8.GetBytes("""[{"key":"streaming","name":"Streaming","color":"#FF6400F6"}]"""),
            Encoding.UTF8.GetBytes("""
                [{"key":"a-replaced","name":"A replaced","categories":["streaming"]},
                 {"key":"b-replaced","name":"B replaced","categories":["streaming"]},
                 {"key":"tinder-k3f9qa","name":"Tinder","categories":["streaming"]}]
                """));
        var (synced, syncFailure) = await app.InvokeAsync(app.AdultA, new SyncCatalogCommand());
        Assert.Null(syncFailure);

        var (answer, failure) = await app.InvokeAsync(app.AdultA, new GetCatalogRunsQuery());

        Assert.Null(failure);
        var report = Assert.Single(((CatalogRunsView)answer!).Runs).Report!;
        Assert.Equal(["a-private", "a-visible"], report.AdoptedRemaining);
        Assert.Equal(["a-replaced"], report.AdoptedReplaced);
        Assert.Empty(report.Conflicts);
        foreach (var serialised in new[] { JsonSerializer.Serialize(answer), JsonSerializer.Serialize(synced) })
        foreach (var hidden in new[] { "b-secret", "b-replaced", "tinder-k3f9qa" })
            Assert.DoesNotContain(hidden, serialised, StringComparison.Ordinal);

        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        var stored = (await subs.CatalogSyncRuns.AsNoTracking().SingleAsync()).Report!;
        foreach (var key in new[] { "b-secret", "b-replaced", "tinder-k3f9qa" })
            Assert.Contains(key, stored, StringComparison.Ordinal); // the host's copy stays complete
    }

    [Fact]
    public async Task The_runs_come_with_the_pinned_commit_newest_first_and_at_most_fifty()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using (var seed = PostgresFixture.NewSubscriptionsContext(app.ConnectionString))
        {
            var start = Instant.FromUtc(2025, 10, 1, 7, 0);
            for (var i = 1; i <= 51; i++)
                seed.CatalogSyncRuns.Add(new CatalogSyncRunRow
                {
                    Id = Guid.CreateVersion7(), StartedAt = start + Duration.FromMinutes(i), FinishedAt = start + Duration.FromMinutes(i),
                    Status = "succeeded", Commit = $"c{i}",
                });
            await seed.SaveChangesAsync();
        }

        var (answer, failure) = await app.InvokeAsync(app.AdultA, new GetCatalogRunsQuery());

        Assert.Null(failure);
        var runs = (CatalogRunsView)answer!;
        Assert.Equal(CatalogFixtures.PinnedCommit, runs.PinnedCommit);
        Assert.Equal(CatalogLimits.RecentRuns, runs.Runs.Count);
        Assert.Equal(("c51", "c2"), (runs.Runs[0].Commit, runs.Runs[^1].Commit));
    }
}
