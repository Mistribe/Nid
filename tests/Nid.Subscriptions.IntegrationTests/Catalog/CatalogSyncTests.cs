using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Subscriptions.IntegrationTests.Catalog;

public class CatalogSyncTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<Result<CatalogSyncRun>> SyncAsync(NidApp app, CancellationToken ct = default)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System; // the catalog job runs as the system (1F3a U4)
        return await scope.ServiceProvider.GetRequiredService<CatalogSync>().RunAsync(ct);
    }

    private static void ServeRealFiles(NidApp app) => app.Catalog.Serve(CatalogFixtures.RealLabels(), CatalogFixtures.RealProviders());

    [Fact]
    public async Task A_sync_of_the_pinned_commit_writes_the_catalog_and_journals_its_report()
    {
        await using var app = await NidApp.StartAsync(fixture);
        ServeRealFiles(app);

        var run = (await SyncAsync(app)).Ok();

        Assert.Equal((CatalogRunStatus.Succeeded, CatalogFixtures.PinnedCommit), (run.Status, run.Commit));
        Assert.Equal(17, run.Report!.ProvidersAdded.Count);
        Assert.Equal([CatalogFixtures.PinnedCommit], app.Catalog.Requested);
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        Assert.Equal(17, await subs.Providers.CountAsync(p => p.Origin == "catalog"));
        var journaled = await subs.CatalogSyncRuns.AsNoTracking().SingleAsync();
        Assert.Equal(("succeeded", CatalogFixtures.PinnedCommit), (journaled.Status, journaled.Commit));
        Assert.NotNull(journaled.Report);
    }

    [Fact]
    public async Task The_same_commit_twice_comes_out_unchanged()
    {
        await using var app = await NidApp.StartAsync(fixture);
        ServeRealFiles(app);
        (await SyncAsync(app)).Ok();

        var second = (await SyncAsync(app)).Ok();

        Assert.Equal(17, second.Report!.ProvidersUnchanged.Count);
        Assert.Empty(second.Report.ProvidersAdded);
        Assert.Empty(second.Report.ProvidersUpdated);
    }

    [Fact]
    public async Task An_unreachable_source_fails_the_run_and_writes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture); // offline, like every test host

        var run = (await SyncAsync(app)).Ok();

        Assert.Equal((CatalogRunStatus.Failed, CatalogCodes.SourceUnavailable), (run.Status, run.FailureCode));
        Assert.Null(run.Report);
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        Assert.False(await subs.Providers.AnyAsync());
    }

    [Fact]
    public async Task An_unreadable_file_fails_the_run_and_writes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        app.Catalog.Serve(CatalogFixtures.RealLabels(), "{\"not\":\"an array\"}"u8.ToArray());

        var run = (await SyncAsync(app)).Ok();

        Assert.Equal((CatalogRunStatus.Failed, CatalogCodes.SourceMalformed), (run.Status, run.FailureCode));
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        Assert.Equal([CatalogLimits.OtherLabel], await subs.Labels.Select(l => l.Key).ToListAsync()); // not even the labels
    }

    [Fact]
    public async Task A_sync_while_one_runs_is_refused_before_fetching()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ICatalogRunLog>().TryStartAsync("another", default);

        Assert.Equal(CatalogCodes.SyncRunning, (await SyncAsync(app)).FailureCode());
        Assert.Empty(app.Catalog.Requested);
    }

    [Fact]
    public async Task A_sync_cut_by_a_shutdown_is_closed_as_interrupted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        ServeRealFiles(app);
        using var shutdown = new CancellationTokenSource();
        app.Catalog.Before = async ct =>
        {
            await shutdown.CancelAsync(); // the host stops while the files are on their way
            await Task.Delay(Timeout.Infinite, ct);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SyncAsync(app, shutdown.Token));

        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        var run = await subs.CatalogSyncRuns.AsNoTracking().SingleAsync();
        Assert.Equal(("failed", CatalogCodes.RunInterrupted), (run.Status, run.FailureCode));
        Assert.False(await subs.Providers.AnyAsync());
    }

    [Fact]
    public async Task Old_runs_are_purged_after_a_sync_but_the_latest_success_stays()
    {
        await using var app = await NidApp.StartAsync(fixture); // today is 2025-10-15: both rows are over 400 days old
        await using (var seed = PostgresFixture.NewSubscriptionsContext(app.ConnectionString))
        {
            seed.CatalogSyncRuns.AddRange(
                new CatalogSyncRunRow
                {
                    Id = Guid.CreateVersion7(), StartedAt = Instant.FromUtc(2024, 1, 1, 7, 0), FinishedAt = Instant.FromUtc(2024, 1, 1, 7, 1),
                    Status = "failed", Commit = "old", FailureCode = CatalogCodes.SourceUnavailable,
                },
                new CatalogSyncRunRow
                {
                    Id = Guid.CreateVersion7(), StartedAt = Instant.FromUtc(2024, 2, 1, 7, 0), FinishedAt = Instant.FromUtc(2024, 2, 1, 7, 1),
                    Status = "succeeded", Commit = "old",
                });
            await seed.SaveChangesAsync();
        }

        (await SyncAsync(app)).Ok(); // fails offline, then purges

        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        var left = await subs.CatalogSyncRuns.AsNoTracking().OrderBy(r => r.StartedAt).Select(r => r.Status).ToListAsync();
        Assert.Equal(["succeeded", "failed"], left);
    }
}
