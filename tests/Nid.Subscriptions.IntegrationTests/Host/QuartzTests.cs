using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests.Host;

public class QuartzTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
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
        Assert.Equal(["catalog-sync", "fx-backfill", "fx-daily", "irl-weekly", "notifications-evaluate", "prices-daily"], jobs);
        Assert.Equal(1, await core.Database
            .SqlQueryRaw<int>("select count(*)::int as \"Value\" from platform.qrtz_cron_triggers where cron_expression = '0 0 7 * * ?'")
            .SingleAsync());
    }

    [Fact]
    public async Task Without_a_succeeded_sync_the_catalog_syncs_by_itself_at_startup()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: JobsOn());
        app.Catalog.Serve(CatalogFixtures.RealLabels(), CatalogFixtures.RealProviders());

        _ = app.Services; // starts the host, hence the scheduler and the startup check

        Assert.Equal("succeeded", await WaitForCatalogRunAsync(app, TimeSpan.FromSeconds(60)));
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        Assert.True(await subs.Providers.AnyAsync(p => p.Key == "netflix" && p.Origin == "catalog"));
    }

    private static async Task<string> WaitForCatalogRunAsync(NidApp app, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
            var run = await subs.CatalogSyncRuns.AsNoTracking().Where(r => r.Status != "running").FirstOrDefaultAsync();
            if (run is not null)
                return run.Status;
            await Task.Delay(250);
        }

        throw new TimeoutException("No catalog sync finished: the scheduler never fired catalog-sync.");
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
