using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;
using Duration = NodaTime.Duration;

namespace Nid.Core.IntegrationTests;

[Collection("postgres")]
public class FxRunLogTests(PostgresFixture postgres)
{
    private sealed class TestClock(Instant now) : IClock
    {
        public Instant Now { get; set; } = now;
        public Instant GetCurrentInstant() => Now;
    }

    private static readonly Instant Start = Instant.FromUtc(2025, 10, 15, 7, 0);

    [Fact]
    public async Task Only_one_run_at_a_time()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var log = new FxRunLog(db, new TestClock(Start));

        var first = await log.TryStartAsync(FxRunKind.Daily, TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        Assert.True(await log.IsRunningAsync(TestContext.Current.CancellationToken));
        Assert.Null(await log.TryStartAsync(FxRunKind.Backfill, TestContext.Current.CancellationToken));

        await log.FinishAsync(first with { Status = FxRunStatus.Succeeded, FinishedAt = Start, FilesRead = 1, RowsWritten = 3 }, TestContext.Current.CancellationToken);
        Assert.False(await log.IsRunningAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(await log.TryStartAsync(FxRunKind.Backfill, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_run_left_running_for_an_hour_is_closed_as_abandoned()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var clock = new TestClock(Start);
        var log = new FxRunLog(db, clock);
        await log.TryStartAsync(FxRunKind.Daily, TestContext.Current.CancellationToken);

        clock.Now = Start + Duration.FromMinutes(61);
        Assert.NotNull(await log.TryStartAsync(FxRunKind.Daily, TestContext.Current.CancellationToken));

        var abandoned = (await log.RecentAsync(10, TestContext.Current.CancellationToken)).Single(r => r.StartedAt == Start);
        Assert.Equal((FxRunStatus.Failed, FxCodes.RunAbandoned), (abandoned.Status, abandoned.FailureCode));
    }

    [Fact]
    public async Task Rejections_round_trip_and_old_runs_are_purged()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var clock = new TestClock(Start);
        var log = new FxRunLog(db, clock);
        var run = (await log.TryStartAsync(FxRunKind.Daily, TestContext.Current.CancellationToken))!;
        var rejection = new FxRejection(new LocalDate(2025, 10, 14), "ARS", 2000m, 1500m, FxCodes.ImplausibleChange);
        await log.FinishAsync(run with { Status = FxRunStatus.Succeeded, FinishedAt = Start, Rejected = [rejection] }, TestContext.Current.CancellationToken);

        Assert.Equal([rejection], Assert.Single(await log.RecentAsync(10, TestContext.Current.CancellationToken)).Rejected);

        Assert.Equal(1, await log.PurgeBeforeAsync(Start + Duration.FromSeconds(1), TestContext.Current.CancellationToken));
        Assert.Empty(await log.RecentAsync(10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_purge_keeps_the_latest_succeeded_backfill()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var clock = new TestClock(Start);
        var log = new FxRunLog(db, clock);
        foreach (var kind in new[] { FxRunKind.Backfill, FxRunKind.Backfill, FxRunKind.Daily })
        {
            var run = (await log.TryStartAsync(kind, TestContext.Current.CancellationToken))!;
            await log.FinishAsync(run with { Status = FxRunStatus.Succeeded, FinishedAt = clock.Now }, TestContext.Current.CancellationToken);
            clock.Now += Duration.FromMinutes(1);
        }

        Assert.Equal(2, await log.PurgeBeforeAsync(clock.Now, TestContext.Current.CancellationToken));

        var kept = Assert.Single(await log.RecentAsync(10, TestContext.Current.CancellationToken));
        Assert.Equal((FxRunKind.Backfill, Start + Duration.FromMinutes(1)), (kept.Kind, kept.StartedAt));
        Assert.True(await log.HasSucceededAsync(FxRunKind.Backfill, TestContext.Current.CancellationToken));
        Assert.False(await log.HasSucceededAsync(FxRunKind.Daily, TestContext.Current.CancellationToken));
    }
}
