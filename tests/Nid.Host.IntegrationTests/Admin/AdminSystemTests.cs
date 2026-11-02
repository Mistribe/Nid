using System.Net;
using System.Text.Json;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Admin;

public class AdminSystemTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    public sealed record Job(string Key, string? Cron, DateTimeOffset? NextRunAt);
    public sealed record Health(string Database, string IdentityProvider);
    public sealed record SystemStatus(string Version, DateTimeOffset StartedAt, Health Health, long? DatabaseSizeBytes);

    [Fact]
    public async Task The_system_status_reports_the_version_the_health_and_the_database_size()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var status = await app.GetAsync<SystemStatus>(app.AdultA, "/api/v1/admin/system");

        Assert.False(string.IsNullOrWhiteSpace(status.Version));
        Assert.Equal(new Health("healthy", "healthy"), status.Health);
        Assert.True(status.DatabaseSizeBytes > 0);
    }

    [Fact]
    public async Task With_jobs_disabled_the_list_is_empty()
    {
        await using var app = await NidApp.StartAsync(fixture);

        Assert.Empty(await app.GetAsync<List<Job>>(app.AdultA, "/api/v1/admin/jobs"));
    }

    [Fact]
    public async Task With_jobs_enabled_the_daily_collection_shows_its_next_run()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string>
        {
            ["Jobs:Enabled"] = "true",
            // Quartz keeps schedulers in a process-wide registry by name: one name per test host.
            ["Jobs:SchedulerName"] = "nid-test-" + Guid.NewGuid().ToString("N"),
        });

        var jobs = await app.GetAsync<List<Job>>(app.AdultA, "/api/v1/admin/jobs");

        Assert.Contains(jobs, j => j.Key == "fx-daily" && j.NextRunAt is not null);
        Assert.Contains(jobs, j => j.Key == "fx-backfill");
        Assert.Contains(jobs, j => j.Key == "catalog-sync");

        // Fired on demand only: once the startup sync's one-shot trigger has fired (no sync has succeeded on an empty
        // database), catalog-sync has no next run.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (jobs.Single(j => j.Key == "catalog-sync").NextRunAt is not null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            jobs = await app.GetAsync<List<Job>>(app.AdultA, "/api/v1/admin/jobs");
        }
        Assert.Contains(jobs, j => j.Key == "catalog-sync" && j.NextRunAt is null);
    }

    [Fact]
    public async Task Jobs_and_system_are_refused_to_an_adult_who_is_not_admin()
    {
        await using var app = await NidApp.StartAsync(fixture);

        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Get, "/api/v1/admin/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Get, "/api/v1/admin/system")).StatusCode);
    }
}
