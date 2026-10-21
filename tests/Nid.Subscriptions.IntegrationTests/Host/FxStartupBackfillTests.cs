using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Host.Jobs;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Subscriptions.IntegrationTests.Host;

/// <summary>
/// The startup check on its own, against the host's real stores: the backfill is owed until one has succeeded,
/// whatever the daily job wrote in the meantime (a failed or interrupted ECB fetch must be retried).
/// </summary>
public class FxStartupBackfillTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed class RecordingTrigger : IFxJobTrigger
    {
        public int Triggered { get; private set; }

        public Task<Result> TriggerBackfillAsync(CancellationToken ct)
        {
            Triggered++;
            return Task.FromResult(Result.Success());
        }
    }

    private static async Task<int> StartupTriggersAsync(NidApp app)
    {
        var trigger = new RecordingTrigger();
        await new FxStartupBackfill(app.Services.GetRequiredService<IServiceScopeFactory>(), trigger).StartAsync(default);
        return trigger.Triggered;
    }

    private static async Task SeedSubTrackerRateAsync(NidApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IFxRateStore>()
            .WriteAsync([new FxRate(new LocalDate(2025, 10, 14), Currency.Eur, 0.86m, FxRateSource.SubTrackerData)], default);
    }

    private static async Task JournalBackfillAsync(NidApp app, FxRunStatus status)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var runs = scope.ServiceProvider.GetRequiredService<IFxRunLog>();
        var run = (await runs.TryStartAsync(FxRunKind.Backfill, default))!;
        await runs.FinishAsync(run with { Status = status, FinishedAt = run.StartedAt }, default);
    }

    [Fact]
    public async Task Rates_without_a_succeeded_backfill_still_start_the_backfill()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SeedSubTrackerRateAsync(app);
        await JournalBackfillAsync(app, FxRunStatus.Failed); // the ECB fetch failed, the daily job filled SubTracker since

        Assert.Equal(1, await StartupTriggersAsync(app));
    }

    [Fact]
    public async Task A_succeeded_backfill_is_not_run_again_at_startup()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SeedSubTrackerRateAsync(app);
        await JournalBackfillAsync(app, FxRunStatus.Succeeded);

        Assert.Equal(0, await StartupTriggersAsync(app));
    }
}
