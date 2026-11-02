using Microsoft.Extensions.DependencyInjection;
using Nid.Platform.Admin;
using Nid.TestSupport;

namespace Nid.Platform.IntegrationTests;

/// <summary>Admin jobs design §2: the runs journal.</summary>
public class JobRunStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 4, 30, 0, TimeSpan.Zero);

    private static async Task<T> WithStoreAsync<T>(NidApp app, Func<IJobRunStore, Task<T>> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IJobRunStore>());
    }

    private static Task<Guid> RunAsync(NidApp app, string job, DateTimeOffset at, string outcome = JobOutcomes.Succeeded)
        => WithStoreAsync(app, async store =>
        {
            var id = Guid.NewGuid();
            await store.StartAsync(id, job, JobTriggers.Scheduled, at, default);
            await store.FinishAsync(id, outcome, null, false, at.AddSeconds(2), default);
            return id;
        });

    [Fact]
    public async Task A_started_run_is_running_then_finished_with_its_outcome_and_duration()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = Guid.NewGuid();

        var running = await WithStoreAsync(app, async store =>
        {
            await store.StartAsync(id, "fx-daily", JobTriggers.OnDemand, T0, default);
            return await store.ListAsync("fx-daily", 100, default);
        });
        Assert.Equal(JobOutcomes.Running, Assert.Single(running).Outcome);
        Assert.Null(running[0].DurationMs);

        var runs = await WithStoreAsync(app, async store =>
        {
            await store.FinishAsync(id, JobOutcomes.Failed, "InvalidOperationException: boom", true, T0.AddMilliseconds(1500), default);
            return await store.ListAsync("fx-daily", 100, default);
        });
        var run = Assert.Single(runs);
        Assert.Equal((JobOutcomes.Failed, "InvalidOperationException: boom", JobTriggers.OnDemand, 1500L), (run.Outcome, run.Summary, run.Trigger, run.DurationMs));
        Assert.Equal(T0.AddMilliseconds(1500), run.FinishedAt);
        Assert.True((await WithStoreAsync(app, s => s.FindAsync("fx-daily", id, default)))!.Value.HasLogs);
    }

    [Fact]
    public async Task Last_runs_give_the_latest_run_of_each_job()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await RunAsync(app, "a", T0);
        var later = await RunAsync(app, "a", T0.AddHours(1), JobOutcomes.Failed);
        var b = await RunAsync(app, "b", T0);

        var last = await WithStoreAsync(app, s => s.LastRunsAsync(default));

        Assert.Equal(["a", "b"], last.Keys.Order());
        Assert.Equal((later, JobOutcomes.Failed), (last["a"].Id, last["a"].Outcome));
        Assert.Equal(b, last["b"].Id);
    }

    [Fact]
    public async Task Purge_keeps_the_most_recent_runs_of_that_job_only()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var a = new List<Guid>();
        for (var i = 0; i < 5; i++) a.Add(await RunAsync(app, "a", T0.AddMinutes(i)));
        for (var i = 0; i < 2; i++) await RunAsync(app, "b", T0.AddMinutes(i));

        var purged = await WithStoreAsync(app, s => s.PurgeAsync("a", 3, default));

        Assert.Equal(a.Take(2).Order(), purged.Order());
        Assert.Equal(a.Skip(2).Reverse(), (await WithStoreAsync(app, s => s.ListAsync("a", 100, default))).Select(r => r.Id));
        Assert.Equal(2, (await WithStoreAsync(app, s => s.ListAsync("b", 100, default))).Count);
    }

    [Fact]
    public async Task Stale_running_rows_are_closed_as_interrupted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = Guid.NewGuid();
        await WithStoreAsync(app, async s => { await s.StartAsync(id, "a", JobTriggers.Scheduled, T0, default); return 0; });

        Assert.Equal(1, await WithStoreAsync(app, s => s.CloseStaleAsync(default)));

        var run = Assert.Single(await WithStoreAsync(app, s => s.ListAsync("a", 100, default)));
        Assert.Equal((JobOutcomes.Interrupted, T0, 0L), (run.Outcome, run.FinishedAt, run.DurationMs));
    }

    [Fact]
    public async Task Find_is_null_for_a_run_of_another_job()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await RunAsync(app, "a", T0);

        Assert.Null(await WithStoreAsync(app, s => s.FindAsync("b", id, default)));
        Assert.Equal(id, (await WithStoreAsync(app, s => s.FindAsync("a", id, default)))!.Value.Run.Id);
    }
}
