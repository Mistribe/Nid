using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Nid.Platform.Admin;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Admin;

/// <summary>Admin jobs design §3.4: the list, a job's runs, a run's logs; admins only.</summary>
public class AdminJobRunsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    public sealed record Run(Guid Id, string Trigger, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, long? DurationMs, string Outcome, string? Summary);
    public sealed record Job(string Key, string? Cron, DateTimeOffset? NextRunAt, Run? LastRun);
    public sealed record JobDetail(string Key, string? Cron, DateTimeOffset? NextRunAt, List<Run> Runs);
    public sealed record RunDetail(Guid Id, string Trigger, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, long? DurationMs, string Outcome, string? Summary, List<string>? Logs, bool LogsAvailable);

    private static readonly DateTimeOffset T0 = new(2020, 1, 1, 4, 30, 0, TimeSpan.Zero);
    private readonly string _logs = Directory.CreateTempSubdirectory("nid-logs-").FullName;

    private Task<NidApp> StartAsync(bool jobs = true) => NidApp.StartAsync(fixture, settings: jobs
        ? new Dictionary<string, string> { ["Jobs:Enabled"] = "true", ["Jobs:SchedulerName"] = "nid-test-" + Guid.NewGuid().ToString("N"), ["FileLogs:Path"] = _logs }
        : new Dictionary<string, string> { ["FileLogs:Path"] = _logs });

    private static async Task<Guid> SeedAsync(NidApp app, string job, DateTimeOffset at, string outcome, string? summary = null, bool hasLogs = false)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IJobRunStore>();
        var id = Guid.NewGuid();
        await store.StartAsync(id, job, JobTriggers.Scheduled, at, default);
        await store.FinishAsync(id, outcome, summary, hasLogs, at.AddMilliseconds(1200), default);
        return id;
    }

    [Fact]
    public async Task The_list_gives_each_job_its_cron_and_last_run()
    {
        await using var app = await StartAsync();
        await SeedAsync(app, "irl-weekly", T0, JobOutcomes.Succeeded);
        var failed = await SeedAsync(app, "irl-weekly", T0.AddDays(7), JobOutcomes.Failed, "InvalidOperationException: boom");

        var jobs = await app.GetAsync<List<Job>>(app.AdultA, "/api/v1/admin/jobs");

        Assert.Equal("0 0 7 * * ?", jobs.Single(j => j.Key == "fx-daily").Cron);
        Assert.Null(jobs.Single(j => j.Key == "fx-backfill").Cron);
        var irl = jobs.Single(j => j.Key == "irl-weekly");
        Assert.Equal((failed, JobOutcomes.Failed, 1200L), (irl.LastRun!.Id, irl.LastRun.Outcome, irl.LastRun.DurationMs));
        Assert.Equal(jobs.Select(j => j.Key).Order(StringComparer.Ordinal), jobs.Select(j => j.Key));
    }

    [Fact]
    public async Task The_detail_lists_runs_newest_first()
    {
        await using var app = await StartAsync();
        var first = await SeedAsync(app, "irl-weekly", T0, JobOutcomes.Succeeded);
        var second = await SeedAsync(app, "irl-weekly", T0.AddDays(7), JobOutcomes.Skipped, "realestate.irl_disabled");

        var detail = await app.GetAsync<JobDetail>(app.AdultA, "/api/v1/admin/jobs/irl-weekly");

        Assert.Equal("irl-weekly", detail.Key);
        Assert.NotNull(detail.Cron);
        Assert.Equal([second, first], detail.Runs.Where(r => r.StartedAt.Year == 2020).Select(r => r.Id));
    }

    [Fact]
    public async Task A_run_gives_its_summary_and_logs()
    {
        await using var app = await StartAsync();
        var withLogs = await SeedAsync(app, "irl-weekly", T0, JobOutcomes.Failed, "InvalidOperationException: boom", hasLogs: true);
        var withoutLogs = await SeedAsync(app, "irl-weekly", T0.AddDays(1), JobOutcomes.Succeeded);
        Directory.CreateDirectory(Path.Combine(_logs, "jobs", "irl-weekly"));
        await File.WriteAllLinesAsync(Path.Combine(_logs, "jobs", "irl-weekly", $"{withLogs}.log"), ["line one", "line two"], TestContext.Current.CancellationToken);

        var run = await app.GetAsync<RunDetail>(app.AdultA, $"/api/v1/admin/jobs/irl-weekly/runs/{withLogs}");
        var bare = await app.GetAsync<RunDetail>(app.AdultA, $"/api/v1/admin/jobs/irl-weekly/runs/{withoutLogs}");

        Assert.Equal(("InvalidOperationException: boom", true), (run.Summary, run.LogsAvailable));
        Assert.Equal(["line one", "line two"], run.Logs!);
        Assert.Equal(((List<string>?)null, true), (bare.Logs, bare.LogsAvailable));
    }

    [Fact]
    public async Task A_run_still_running_or_left_by_a_dead_process_shows_the_lines_it_wrote()
    {
        await using var app = await StartAsync();
        var id = Guid.NewGuid();
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IJobRunStore>().StartAsync(id, "irl-weekly", JobTriggers.Scheduled, T0, TestContext.Current.CancellationToken);
        var folder = app.Services.GetRequiredService<Nid.Host.Jobs.RunLogFolder>();
        using var writer = folder.Open("irl-weekly", id)!;
        writer.Write("still going");

        var running = await app.GetAsync<RunDetail>(app.AdultA, $"/api/v1/admin/jobs/irl-weekly/runs/{id}");
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IJobRunStore>().CloseStaleAsync(TestContext.Current.CancellationToken);
        var interrupted = await app.GetAsync<RunDetail>(app.AdultA, $"/api/v1/admin/jobs/irl-weekly/runs/{id}");

        Assert.Equal((JobOutcomes.Running, "still going"), (running.Outcome, Assert.Single(running.Logs!)));
        Assert.Equal((JobOutcomes.Interrupted, "still going"), (interrupted.Outcome, Assert.Single(interrupted.Logs!)));
    }

    [Fact]
    public async Task A_run_of_another_job_or_an_unknown_key_is_404()
    {
        await using var app = await StartAsync();
        var irl = await SeedAsync(app, "irl-weekly", T0, JobOutcomes.Succeeded);

        foreach (var url in new[] { "/api/v1/admin/jobs/nope", $"/api/v1/admin/jobs/fx-daily/runs/{irl}", $"/api/v1/admin/jobs/..%2Fx/runs/{irl}", $"/api/v1/admin/jobs/irl-weekly/runs/{Guid.NewGuid()}" })
            Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, url)).StatusCode);
    }

    [Fact]
    public async Task Non_admins_get_403()
    {
        await using var app = await StartAsync();
        var irl = await SeedAsync(app, "irl-weekly", T0, JobOutcomes.Succeeded);

        foreach (var url in new[] { "/api/v1/admin/jobs", "/api/v1/admin/jobs/irl-weekly", $"/api/v1/admin/jobs/irl-weekly/runs/{irl}" })
            Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Get, url)).StatusCode);
    }

    [Fact]
    public async Task With_jobs_disabled_the_list_is_empty_and_details_are_404()
    {
        await using var app = await StartAsync(jobs: false);
        var irl = await SeedAsync(app, "irl-weekly", T0, JobOutcomes.Succeeded);

        Assert.Empty(await app.GetAsync<List<Job>>(app.AdultA, "/api/v1/admin/jobs"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/admin/jobs/irl-weekly")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/admin/jobs/irl-weekly/runs/{irl}")).StatusCode);
    }
}
