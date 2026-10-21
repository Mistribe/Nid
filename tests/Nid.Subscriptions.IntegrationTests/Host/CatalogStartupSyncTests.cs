using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Host.Jobs;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;
using Duration = NodaTime.Duration;

namespace Nid.Subscriptions.IntegrationTests.Host;

/// <summary>Design §4 "Trigger": at startup, a sync is owed until one has succeeded — failed ones do not count.</summary>
public class CatalogStartupSyncTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed class RecordingTrigger : ICatalogJobTrigger
    {
        public int Triggered { get; private set; }

        public Task<Result> TriggerSyncAsync(CancellationToken ct)
        {
            Triggered++;
            return Task.FromResult(Result.Success());
        }
    }

    private static async Task<int> StartupTriggersAsync(NidApp app)
    {
        var trigger = new RecordingTrigger();
        await new CatalogStartupSync(app.Services.GetRequiredService<IServiceScopeFactory>(), trigger).StartAsync(default);
        return trigger.Triggered;
    }

    private static async Task JournalAsync(NidApp app, CatalogRunStatus status)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var runs = scope.ServiceProvider.GetRequiredService<ICatalogRunLog>();
        var run = (await runs.TryStartAsync(CatalogFixtures.PinnedCommit, default))!;
        await runs.FinishAsync(run with { Status = status, FinishedAt = run.StartedAt }, default);
    }

    [Fact]
    public async Task Without_any_sync_the_startup_triggers_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        Assert.Equal(1, await StartupTriggersAsync(app));
    }

    [Fact]
    public async Task After_failed_syncs_only_the_startup_still_triggers_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await JournalAsync(app, CatalogRunStatus.Failed);
        Assert.Equal(1, await StartupTriggersAsync(app));
    }

    [Fact]
    public async Task A_succeeded_sync_is_not_run_again_at_startup()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await JournalAsync(app, CatalogRunStatus.Succeeded);
        await JournalAsync(app, CatalogRunStatus.Failed); // a later failure does not reopen it
        Assert.Equal(0, await StartupTriggersAsync(app));
    }

    /// <summary>Final review F1: the startup closes a run a crash left running, so the journal shows it failed.</summary>
    [Fact]
    public async Task The_startup_closes_a_run_left_running_for_an_hour()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await JournalAsync(app, CatalogRunStatus.Succeeded);
        var now = app.Services.GetRequiredService<IClock>().GetCurrentInstant();
        var stale = Guid.CreateVersion7();
        await using (var seed = PostgresFixture.NewSubscriptionsContext(app.ConnectionString))
        {
            seed.CatalogSyncRuns.Add(new CatalogSyncRunRow
            {
                Id = stale, StartedAt = now - Duration.FromHours(2), Status = "running", Commit = CatalogFixtures.PinnedCommit,
            });
            await seed.SaveChangesAsync();
        }

        Assert.Equal(0, await StartupTriggersAsync(app));

        await using var check = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        var row = await check.CatalogSyncRuns.AsNoTracking().SingleAsync(r => r.Id == stale);
        Assert.Equal(("failed", CatalogCodes.RunAbandoned), (row.Status, row.FailureCode));
    }
}
