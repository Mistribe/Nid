using Microsoft.Extensions.DependencyInjection;
using Nid.Host.Jobs;
using Nid.Subscriptions.Contracts;
using Nid.TestSupport;
using UnambitiousFx.Functional;

namespace Nid.Subscriptions.IntegrationTests.Host;

/// <summary>Design §4 "Trigger": at startup, a sync is owed until one has succeeded — failed ones do not count.</summary>
[Collection("postgres")]
public class CatalogStartupSyncTests(PostgresFixture fixture)
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
}
