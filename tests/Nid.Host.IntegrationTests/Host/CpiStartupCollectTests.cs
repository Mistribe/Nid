using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Core.Domain.Inflation;
using Nid.Host.Jobs;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>The startup check on its own, against the host's real store: an empty series is collected at once, a filled one waits for the cron.</summary>
public class CpiStartupCollectTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed class RecordingTrigger : ICpiJobTrigger
    {
        public int Triggered { get; private set; }

        public Task<Result> TriggerAsync(CancellationToken ct)
        {
            Triggered++;
            return Task.FromResult(Result.Success());
        }
    }

    private static async Task<int> StartupTriggersAsync(NidApp app)
    {
        var trigger = new RecordingTrigger();
        await new CpiStartupCollect(app.Services.GetRequiredService<IServiceScopeFactory>(), trigger).StartAsync(default);
        return trigger.Triggered;
    }

    [Fact]
    public async Task An_empty_series_is_collected_at_startup()
    {
        await using var app = await NidApp.StartAsync(fixture);

        Assert.Equal(1, await StartupTriggersAsync(app));
    }

    [Fact]
    public async Task A_stored_series_is_left_to_the_cron()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ICpiStore>()
                .UpsertAsync(new CpiValue(new YearMonth(2026, 9), 103m, null, new LocalDate(2026, 10, 1), "manual"), TestContext.Current.CancellationToken);

        Assert.Equal(0, await StartupTriggersAsync(app));
    }
}
