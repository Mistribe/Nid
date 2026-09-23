using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests.Host;

[Collection("postgres")]
public class QuartzTests(PostgresFixture fixture)
{
    private static Dictionary<string, string> JobsOn() => new()
    {
        ["Jobs:Enabled"] = "true",
        // Quartz keeps schedulers in a process-wide registry by name: one name per test host.
        ["Jobs:SchedulerName"] = "nid-test-" + Guid.NewGuid().ToString("N"),
    };

    [Fact]
    public async Task On_an_empty_database_the_backfill_runs_by_itself_through_Quartz_on_PostgreSQL()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: JobsOn());
        app.Sources.FailWith = null;
        app.Sources.Ecb = FxFixtures.EcbZip((new LocalDate(2025, 10, 14), 1.25m, 0.75m));

        _ = app.Services; // starts the host, hence the scheduler and the startup check

        var run = await WaitForRunAsync(app, TimeSpan.FromSeconds(60));
        Assert.Equal(("backfill", "succeeded"), (run.Kind, run.Status));

        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.True(await core.FxRates.AnyAsync(r => r.Quote == "EUR"));

        var jobs = await core.Database
            .SqlQueryRaw<string>("select job_name as \"Value\" from platform.qrtz_job_details order by job_name")
            .ToListAsync();
        Assert.Equal(["fx-backfill", "fx-daily"], jobs);
        Assert.Equal(1, await core.Database
            .SqlQueryRaw<int>("select count(*)::int as \"Value\" from platform.qrtz_cron_triggers where cron_expression = '0 0 7 * * ?'")
            .SingleAsync());
    }

    private static async Task<(string Kind, string Status)> WaitForRunAsync(NidApp app, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
            var run = await core.FxSyncRuns.AsNoTracking().Where(r => r.Status != "running").FirstOrDefaultAsync();
            if (run is not null)
                return (run.Kind, run.Status);
            await Task.Delay(250);
        }

        throw new TimeoutException("No collection run finished: the scheduler never fired the backfill.");
    }
}
