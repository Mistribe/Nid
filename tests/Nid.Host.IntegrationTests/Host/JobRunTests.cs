using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Investments.Contracts;
using Nid.Platform.Admin;
using Nid.Platform.Contracts;
using Nid.Platform.Infrastructure.Persistence;
using Nid.TestSupport;
using Quartz;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>Admin jobs design §3.1–§3.2: every run recorded with its outcome, through Quartz on PostgreSQL.</summary>
public class JobRunTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private readonly string _scheduler = "nid-test-" + Guid.NewGuid().ToString("N");

    private Dictionary<string, string> JobsOn() => new() { ["Jobs:Enabled"] = "true", ["Jobs:SchedulerName"] = _scheduler };

    private async Task<IScheduler> SchedulerAsync(NidApp app)
        => await app.Services.GetRequiredKeyedService<ISchedulerFactory>(_scheduler).GetScheduler();

    private static async Task<JobRunRow> WaitForRunAsync(NidApp app, string job, Func<JobRunRow, bool>? match = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            await using var db = PostgresFixture.NewPlatformContext(app.ConnectionString);
            var run = (await db.JobRuns.AsNoTracking().Where(r => r.JobKey == job && r.Outcome != JobOutcomes.Running).ToListAsync())
                .FirstOrDefault(match ?? (_ => true));
            if (run is not null)
                return run;
            await Task.Delay(250);
        }

        throw new TimeoutException($"No finished run of {job} was recorded.");
    }

    [Fact]
    public async Task An_on_demand_backfill_is_recorded_as_succeeded()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: JobsOn());
        app.Sources.FailWith = null;
        app.Sources.Ecb = FxFixtures.EcbZip((new NodaTime.LocalDate(2025, 10, 14), 1.25m, 0.75m));
        _ = app.Services; // starts the host: the backfill fires at startup, on a one-shot trigger

        var run = await WaitForRunAsync(app, "fx-backfill");

        Assert.Equal((JobTriggers.OnDemand, JobOutcomes.Succeeded, (string?)null), (run.Trigger, run.Outcome, run.Summary));
        Assert.NotNull(run.FinishedAt);
        await using var scope = app.Services.CreateAsyncScope();
        Assert.Equal(run.Id, (await scope.ServiceProvider.GetRequiredService<IJobRunStore>().LastRunsAsync(default))["fx-backfill"].Id);
    }

    [Fact]
    public async Task A_refused_job_is_recorded_as_skipped_with_its_code()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: JobsOn());

        await (await SchedulerAsync(app)).TriggerJob(new JobKey("prices-daily"));

        var run = await WaitForRunAsync(app, "prices-daily");
        Assert.Equal((JobOutcomes.Skipped, PriceCodes.Disabled), (run.Outcome, run.Summary));
    }

    [Fact]
    public async Task A_crashing_job_is_recorded_as_failed_with_its_message()
    {
        await using var app = await NidApp.StartAsync(fixture,
            overrides: services => services.AddScoped<IActorAccessor, ThrowingActorAccessor>(), settings: JobsOn());

        await (await SchedulerAsync(app)).TriggerJob(new JobKey("irl-weekly"));

        var run = await WaitForRunAsync(app, "irl-weekly");
        Assert.Equal((JobOutcomes.Failed, "InvalidOperationException: boom"), (run.Outcome, run.Summary));
    }

    [Fact]
    public async Task A_job_throwing_a_JobExecutionException_is_recorded_as_failed()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: JobsOn());

        await (await SchedulerAsync(app)).ScheduleJob(
            JobBuilder.Create<ThrowingJob>().WithIdentity("test-throwing").Build(),
            TriggerBuilder.Create().StartNow().Build());

        var run = await WaitForRunAsync(app, "test-throwing");
        Assert.Equal((JobOutcomes.Failed, "kaput"), (run.Outcome, run.Summary));
    }

    [Fact]
    public async Task The_101st_run_purges_the_oldest()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: JobsOn());
        var old = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var seeded = new List<Guid>();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IJobRunStore>();
            for (var i = 0; i < 100; i++)
            {
                var id = Guid.NewGuid();
                await store.StartAsync(id, "irl-weekly", JobTriggers.Scheduled, old.AddDays(i), default);
                await store.FinishAsync(id, JobOutcomes.Succeeded, null, false, old.AddDays(i).AddSeconds(1), default);
                seeded.Add(id);
            }
        }

        await (await SchedulerAsync(app)).TriggerJob(new JobKey("irl-weekly"));
        var fresh = await WaitForRunAsync(app, "irl-weekly", r => !seeded.Contains(r.Id));

        await using var db = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var ids = await db.JobRuns.Where(r => r.JobKey == "irl-weekly").Select(r => r.Id).ToListAsync();
        Assert.Equal(100, ids.Count);
        Assert.Contains(fresh.Id, ids);
        Assert.DoesNotContain(seeded[0], ids);
    }

    [Fact]
    public async Task Each_run_writes_its_own_filtered_file_and_the_purge_removes_it()
    {
        var logs = Directory.CreateTempSubdirectory("nid-logs-").FullName;
        var settings = JobsOn();
        settings["FileLogs:Path"] = logs;
        await using var app = await NidApp.StartAsync(fixture, overrides: services => services.AddScoped<IActorAccessor, ThrowingActorAccessor>(), settings: settings);
        var old = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var oldest = Guid.NewGuid();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IJobRunStore>();
            for (var i = 0; i < 100; i++)
            {
                var id = i == 0 ? oldest : Guid.NewGuid();
                await store.StartAsync(id, "irl-weekly", JobTriggers.Scheduled, old.AddDays(i), default);
                await store.FinishAsync(id, JobOutcomes.Succeeded, null, true, old.AddDays(i), default);
            }
        }
        var oldestFile = Path.Combine(logs, "jobs", "irl-weekly", $"{oldest}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(oldestFile)!);
        await File.WriteAllTextAsync(oldestFile, "old");

        var scheduler = await SchedulerAsync(app);
        await Task.WhenAll(scheduler.TriggerJob(new JobKey("irl-weekly")).AsTask(), scheduler.TriggerJob(new JobKey("prices-daily")).AsTask());
        var irl = await WaitForRunAsync(app, "irl-weekly", r => r.StartedAt.ToDateTimeOffset().Year > 2020);
        var prices = await WaitForRunAsync(app, "prices-daily");

        Assert.True(irl.HasLogs && prices.HasLogs);
        var irlLog = await File.ReadAllTextAsync(Path.Combine(logs, "jobs", "irl-weekly", $"{irl.Id}.log"));
        var pricesLog = await File.ReadAllTextAsync(Path.Combine(logs, "jobs", "prices-daily", $"{prices.Id}.log"));
        Assert.Contains("irl-weekly crashed InvalidOperationException: boom", irlLog);
        Assert.DoesNotContain("prices-daily", irlLog);
        Assert.Contains("prices-daily crashed", pricesLog);
        Assert.DoesNotContain("irl-weekly", pricesLog);
        Assert.False(File.Exists(oldestFile));
    }

    private sealed class ThrowingActorAccessor : IActorAccessor
    {
        public Actor? Current { get => null; set => throw new InvalidOperationException("boom"); }
    }
}

public sealed class ThrowingJob : IJob
{
    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
        => throw new JobExecutionException("kaput");
}
