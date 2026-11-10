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
using UnambitiousFx.Synapse.Abstractions;
using Duration = NodaTime.Duration;

namespace Nid.Subscriptions.IntegrationTests.Catalog;

public class CatalogRequestTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
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

        var (run, failure) = await app.InvokeAsync(app.AdultA, new SyncCatalogCommand(), TestContext.Current.CancellationToken);

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

        var (run, _) = await app.InvokeAsync(app.AdultA, new SyncCatalogCommand(), TestContext.Current.CancellationToken);

        var view = (CatalogSyncRunView)run!;
        Assert.Equal("succeeded", view.Status);
        Assert.Equal(17, view.Report!.ProvidersAdded.Count);
        Assert.NotNull(view.FinishedAt);
    }

    [Fact]
    public async Task Without_jobs_a_sync_request_says_so()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (_, failure) = await app.InvokeAsync(app.AdultA, new RequestCatalogSyncCommand(), TestContext.Current.CancellationToken);
        Assert.Equal(CatalogCodes.JobsDisabled, failure?.Code);
    }

    [Fact]
    public async Task A_sync_request_triggers_the_job_unless_a_sync_is_running()
    {
        var trigger = new RecordingTrigger();
        await using var app = await NidApp.StartAsync(fixture, services => services.AddSingleton<ICatalogJobTrigger>(trigger));

        var (requested, failure) = await app.InvokeAsync(app.AdultA, new RequestCatalogSyncCommand(), TestContext.Current.CancellationToken);
        Assert.Null(failure);
        Assert.Equal(new CatalogSyncRequested(true), requested);
        Assert.Equal(1, trigger.Calls);

        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ICatalogRunLog>().TryStartAsync(CatalogFixtures.PinnedCommit, TestContext.Current.CancellationToken);

        var (_, running) = await app.InvokeAsync(app.AdultA, new RequestCatalogSyncCommand(), TestContext.Current.CancellationToken);
        Assert.Equal(CatalogCodes.SyncRunning, running?.Code);
        Assert.Equal(1, trigger.Calls);
    }

    /// <summary>Spec §6 (amended after 1G): the sync journal and the sync itself are the admin's. B is not admin.</summary>
    [Fact]
    public async Task The_journal_requests_are_refused_to_an_adult_who_is_not_admin()
    {
        await using var app = await NidApp.StartAsync(fixture);

        foreach (var request in new object[] { new SyncCatalogCommand(), new RequestCatalogSyncCommand(), new GetCatalogRunsQuery(null) })
        {
            var (_, failure) = await app.InvokeAsync(app.AdultB, request, TestContext.Current.CancellationToken);
            Assert.Equal(AdminCodes.Required, failure?.Code);
        }

        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        Assert.False(await subs.CatalogSyncRuns.AnyAsync(cancellationToken: TestContext.Current.CancellationToken)); // refused before the handler: no run journaled
    }

    private static readonly byte[] StreamingLabel = Encoding.UTF8.GetBytes("""[{"key":"streaming","name":"Streaming","color":"#FF6400F6"}]""");

    private static readonly byte[] ReplacingCatalog = Encoding.UTF8.GetBytes("""
        [{"key":"a-replaced","name":"A replaced","categories":["streaming"]},
         {"key":"b-replaced","name":"B replaced","categories":["streaming"]},
         {"key":"netflix","name":"Netflix","categories":["streaming"]}]
        """);

    private static readonly string[] ReplacingCatalogKeys = ["a-replaced", "b-replaced", "netflix"];

    private static async Task<CatalogReport> ReportForAsync(NidApp app, PersonId reader, int run = 0)
    {
        var (answer, failure) = await app.InvokeAsync(reader, new GetCatalogRunsQuery(null));
        Assert.Null(failure);
        return ((CatalogRunsView)answer!).Runs.Items[run].Report!;
    }

    private static async Task<CatalogSyncRunView> SyncAsync(NidApp app)
    {
        var (synced, failure) = await app.InvokeAsync(app.AdultA, new SyncCatalogCommand());
        Assert.Null(failure);
        return (CatalogSyncRunView)synced!;
    }

    /// <summary>
    /// A's shared subscription uses the adopted <c>a-replaced</c>. When <paramref name="bUsesCatalogKeys"/>, B's private
    /// subscriptions use the adopted <c>b-replaced</c> (in the catalog) and <c>b-remaining</c> (not in it).
    /// </summary>
    private async Task<NidApp> HouseholdAsync(bool bUsesCatalogKeys)
    {
        var app = await NidApp.StartAsync(fixture);
        await app.SeedProviderAsync("a-replaced", "a-replaced", ProviderOrigin.Adopted);
        await app.LinkProviderAsync(await app.NewSubscriptionAsync("uses a-replaced", app.AdultA, Visibility.Shared), "a-replaced");
        var bFirst = await app.NewSubscriptionAsync("b first", app.AdultB, Visibility.Private);
        var bSecond = await app.NewSubscriptionAsync("b second", app.AdultB, Visibility.Private);
        if (bUsesCatalogKeys)
        {
            await app.SeedProviderAsync("b-replaced", "b-replaced", ProviderOrigin.Adopted);
            await app.SeedProviderAsync("b-remaining", "b-remaining", ProviderOrigin.Adopted);
            await app.LinkProviderAsync(bFirst, "b-replaced");
            await app.LinkProviderAsync(bSecond, "b-remaining");
        }

        app.Catalog.Serve(StreamingLabel, ReplacingCatalog);
        return app;
    }

    /// <summary>
    /// Spec §3.6 (amended after 1G): A is admin, and still reads nothing of B's private subscriptions — not the adopted
    /// key one of them uses, not a custom conflict only B's private subscription uses, not that the catalog replaced a
    /// key B used (it reads as added), and no count of what was dropped. A's own adopted keys, shared or private, stay
    /// listed (a key A's visible subscription used at the sync shows as replaced); the stored report is whole.
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
        var bItems = new List<ItemId>();
        foreach (var (key, origin, owner, visibility) in links)
        {
            await app.SeedProviderAsync(key, key, origin, createdBy: owner);
            var item = await app.NewSubscriptionAsync("uses " + key, owner, visibility);
            await app.LinkProviderAsync(item, key);
            if (owner == app.AdultB)
                bItems.Add(item);
        }

        app.Catalog.Serve(StreamingLabel, Encoding.UTF8.GetBytes("""
            [{"key":"a-replaced","name":"A replaced","categories":["streaming"]},
             {"key":"b-replaced","name":"B replaced","categories":["streaming"]},
             {"key":"tinder-k3f9qa","name":"Tinder","categories":["streaming"]}]
            """));
        var synced = await SyncAsync(app);

        var (answer, failure) = await app.InvokeAsync(app.AdultA, new GetCatalogRunsQuery(null), TestContext.Current.CancellationToken);

        Assert.Null(failure);
        foreach (var report in new[] { Assert.Single(((CatalogRunsView)answer!).Runs.Items).Report!, synced.Report! })
        {
            Assert.Equal(["a-private", "a-visible"], report.AdoptedRemaining);
            Assert.Equal(["a-replaced"], report.AdoptedReplaced);
            Assert.Equal(["b-replaced"], report.ProvidersAdded); // presented as if B had never used it
            Assert.Empty(report.Conflicts);
        }
        foreach (var serialised in new[] { JsonSerializer.Serialize(answer), JsonSerializer.Serialize(synced) })
        {
            foreach (var hidden in new[] { "b-secret", "tinder-k3f9qa" })
                Assert.DoesNotContain(hidden, serialised, StringComparison.Ordinal);
            foreach (var item in bItems)
                Assert.DoesNotContain(item.Value.ToString(), serialised, StringComparison.OrdinalIgnoreCase); // the usage stays the host's
        }

        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        var stored = (await subs.CatalogSyncRuns.AsNoTracking().SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).Report!;
        foreach (var key in new[] { "b-secret", "b-replaced", "tinder-k3f9qa" })
            Assert.Contains(key, stored, StringComparison.Ordinal); // the host's copy stays complete
    }

    /// <summary>Spec §3.6 "Judged as of the sync": A's view of a sync is the same whether or not B's private subscriptions used catalog keys.</summary>
    [Fact]
    public async Task What_B_privately_used_changes_nothing_in_As_view_of_the_sync()
    {
        var views = new List<(string Synced, string Journal)>();
        foreach (var bUses in new[] { true, false })
        {
            await using var app = await HouseholdAsync(bUses);
            var synced = await SyncAsync(app);
            views.Add((JsonSerializer.Serialize(synced.Report), JsonSerializer.Serialize(await ReportForAsync(app, app.AdultA))));
        }

        Assert.Equal(views[1], views[0]);
        Assert.Equal(views[0].Synced, views[0].Journal);
    }

    /// <summary>The catalog is public: every key of the commit is accounted for in A's view, and a second sync shows it unchanged.</summary>
    [Fact]
    public async Task Every_catalog_key_is_accounted_for_in_As_view_and_unchanged_on_the_next_sync()
    {
        await using var app = await HouseholdAsync(bUsesCatalogKeys: true);
        await SyncAsync(app);

        var first = await ReportForAsync(app, app.AdultA);
        var accounted = first.ProvidersAdded.Concat(first.ProvidersUpdated).Concat(first.ProvidersUnchanged).Concat(first.AdoptedReplaced)
            .Concat(first.Refused.Select(r => r.Key ?? "")).Concat(first.Duplicates.Select(d => d.Key)).ToHashSet(StringComparer.Ordinal);
        Assert.Subset(accounted, ReplacingCatalogKeys.ToHashSet(StringComparer.Ordinal));
        Assert.Equal(["a-replaced"], first.AdoptedReplaced);
        Assert.Equal(["b-replaced", "netflix"], first.ProvidersAdded);
        Assert.Empty(first.AdoptedRemaining);

        await SyncAsync(app);

        var second = await ReportForAsync(app, app.AdultA);
        Assert.Equal(ReplacingCatalogKeys, second.ProvidersUnchanged);
        Assert.Empty(second.ProvidersAdded);
        Assert.Empty(second.AdoptedReplaced);
    }

    /// <summary>Linking a subscription to a key after the sync does not reveal whether the household used it then.</summary>
    [Fact]
    public async Task Linking_a_subscription_after_the_sync_changes_nothing_in_its_report()
    {
        await using var app = await HouseholdAsync(bUsesCatalogKeys: true);
        await SyncAsync(app);

        await app.LinkProviderAsync(await app.NewSubscriptionAsync("probe", app.AdultA, Visibility.Private), "b-replaced");

        var report = await ReportForAsync(app, app.AdultA);
        Assert.Equal(["a-replaced"], report.AdoptedReplaced);
        Assert.Equal(["b-replaced", "netflix"], report.ProvidersAdded);
    }

    /// <summary>A report stored before the usage was recorded reads with none: its household entries are hidden from every person.</summary>
    [Fact]
    public async Task A_report_stored_without_its_usage_hides_its_household_entries()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using (var seed = PostgresFixture.NewSubscriptionsContext(app.ConnectionString))
        {
            var bare = CatalogReport.Empty with { ProvidersAdded = ["netflix"], AdoptedReplaced = ["old-replaced"], AdoptedRemaining = ["old-remaining"] };
            seed.CatalogSyncRuns.Add(new CatalogSyncRunRow
            {
                Id = Guid.CreateVersion7(), StartedAt = Instant.FromUtc(2025, 10, 1, 7, 0), FinishedAt = Instant.FromUtc(2025, 10, 1, 7, 1),
                Status = "succeeded", Commit = CatalogFixtures.PinnedCommit,
                Report = JsonSerializer.Serialize(bare, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var report = await ReportForAsync(app, app.AdultA);

        Assert.Equal(["netflix", "old-replaced"], report.ProvidersAdded);
        Assert.Empty(report.AdoptedReplaced);
        Assert.Empty(report.AdoptedRemaining);
    }

    /// <summary>The unfiltered report is the system actor's (the job), never a person's, whatever their role.</summary>
    [Fact]
    public async Task Only_the_system_actor_reads_the_whole_report()
    {
        await using var app = await HouseholdAsync(bUsesCatalogKeys: true);
        await SyncAsync(app);

        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var runs = await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync<CatalogRunsView>(new GetCatalogRunsQuery(null), TestContext.Current.CancellationToken);
        runs.TryGetValue(out var view);

        var whole = Assert.Single(view!.Runs.Items).Report!;
        Assert.Equal(["a-replaced", "b-replaced"], whole.AdoptedReplaced);
        Assert.Equal(["b-remaining"], whole.AdoptedRemaining);
        Assert.Equal(["netflix"], whole.ProvidersAdded);
    }

    [Fact]
    public async Task The_runs_come_with_the_pinned_commit_newest_first_one_page_at_a_time()
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
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var (answer, failure) = await app.InvokeAsync(app.AdultA, new GetCatalogRunsQuery(null), TestContext.Current.CancellationToken);

        Assert.Null(failure);
        var runs = (CatalogRunsView)answer!;
        Assert.Equal(CatalogFixtures.PinnedCommit, runs.PinnedCommit);
        Assert.Equal((50, 51, 1), (runs.Runs.Items.Count, runs.Runs.Total, runs.Runs.Page));
        Assert.Equal(("c51", "c2"), (runs.Runs.Items[0].Commit, runs.Runs.Items[^1].Commit));
        Assert.Equal("c51", runs.Latest!.Commit);
    }
}
