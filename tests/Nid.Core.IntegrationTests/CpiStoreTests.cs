using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Core.Domain.Inflation;
using Nid.Core.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;
using Npgsql;
using Duration = NodaTime.Duration;

namespace Nid.Core.IntegrationTests;

/// <summary>Inflation spec: the stored CPI series and the collection's run log.</summary>
[Collection("postgres")]
public class CpiStoreTests(PostgresFixture postgres)
{
    private sealed class TestClock(Instant now) : IClock
    {
        public Instant Now { get; set; } = now;
        public Instant GetCurrentInstant() => Now;
    }

    private static readonly LocalDate Day = new(2026, 10, 23);

    [Fact]
    public async Task Upsert_then_list_by_month()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var store = new CpiStore(db);

        await store.UpsertAsync(new CpiValue(new YearMonth(2026, 9), 120.5m, new LocalDate(2026, 10, 15), Day, "insee"), TestContext.Current.CancellationToken);
        await store.UpsertAsync(new CpiValue(new YearMonth(2026, 8), 120.1m, null, Day, "manual"), TestContext.Current.CancellationToken);

        Assert.Equal([new YearMonth(2026, 8), new YearMonth(2026, 9)], (await store.ListAsync(TestContext.Current.CancellationToken)).Select(v => v.Month));
    }

    [Fact]
    public async Task Manual_does_not_overwrite_insee()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var store = new CpiStore(db);
        var month = new YearMonth(2026, 9);

        await store.UpsertAsync(new CpiValue(month, 120.5m, null, Day, "insee"), TestContext.Current.CancellationToken);

        Assert.False(await store.UpsertManualAsync(new CpiValue(month, 130m, null, Day, "manual"), TestContext.Current.CancellationToken));
        Assert.Equal(120.5m, Assert.Single(await store.ListAsync(TestContext.Current.CancellationToken)).Value);
    }

    [Fact]
    public async Task One_running_run_at_most()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var log = new CpiRunLog(db, PostgresFixture.Clock);

        Assert.NotNull(await log.TryStartAsync(TestContext.Current.CancellationToken));
        Assert.Null(await log.TryStartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_manual_value_on_a_fresh_month_is_stored_then_replaced_by_insee()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var store = new CpiStore(db);
        var month = new YearMonth(2026, 9);

        Assert.True(await store.UpsertManualAsync(new CpiValue(month, 120m, null, Day, "manual"), TestContext.Current.CancellationToken));
        var manual = Assert.Single(await store.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(("manual", (LocalDate?)null), (manual.Source, manual.PublishedOn));

        await store.UpsertAsync(new CpiValue(month, 120.5m, new LocalDate(2026, 10, 15), Day, "insee"), TestContext.Current.CancellationToken);
        var insee = Assert.Single(await store.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(("insee", 120.5m), (insee.Source, insee.Value));
    }

    [Fact]
    public async Task A_batch_with_one_bad_value_writes_nothing()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var store = new CpiStore(db);

        await Assert.ThrowsAsync<DbUpdateException>(() => store.UpsertManyAsync(
            [new CpiValue(new YearMonth(2026, 8), 120m, null, Day, "insee"), new CpiValue(new YearMonth(2026, 9), 0m, null, Day, "insee")],
            TestContext.Current.CancellationToken));

        Assert.Empty(await store.ListAsync(TestContext.Current.CancellationToken)); // ListAsync reads the database, not the tracker
    }

    [Theory]
    [InlineData("2026-13", 120)]
    [InlineData("2026-9", 120)]
    [InlineData("2026-09", 0)]
    [InlineData("2026-09", -1)]
    public async Task The_database_refuses_a_bad_month_or_a_non_positive_value(string month, int value)
    {
        await using var db = await postgres.NewDatabaseAsync();
        db.Cpi.Add(new CpiRow { Month = month, Value = value, FetchedOn = Day, Source = "insee" });

        var e = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(e.InnerException).SqlState);
    }

    [Fact]
    public async Task A_run_over_an_hour_old_is_closed_as_failed_by_the_next_start()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var clock = new TestClock(Instant.FromUtc(2026, 10, 23, 7, 0));
        var log = new CpiRunLog(db, clock);
        var stale = await log.TryStartAsync(TestContext.Current.CancellationToken);

        clock.Now += Duration.FromMinutes(61);
        var next = await log.TryStartAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(next);
        var old = (await log.RecentAsync(5, TestContext.Current.CancellationToken)).Single(r => r.Id == stale!.Id);
        Assert.Equal("failed", old.Status);
        Assert.NotNull(old.FinishedAt);
    }

    [Fact]
    public async Task Finish_persists_the_counts_and_purge_keeps_running_runs()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var clock = new TestClock(Instant.FromUtc(2026, 1, 1, 7, 0));
        var log = new CpiRunLog(db, clock);
        var done = await log.TryStartAsync(TestContext.Current.CancellationToken);
        await log.FinishAsync(done! with { Status = "succeeded", FinishedAt = done.StartedAt, Added = 2, Replaced = 1, Rejected = 3, Message = "m" }, TestContext.Current.CancellationToken);
        var saved = Assert.Single(await log.RecentAsync(5, TestContext.Current.CancellationToken));
        Assert.Equal(("succeeded", 2, 1, 3, "m"), (saved.Status, saved.Added, saved.Replaced, saved.Rejected, saved.Message));

        Assert.NotNull(await log.TryStartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await log.PurgeBeforeAsync(Instant.FromUtc(2027, 1, 1, 0, 0), TestContext.Current.CancellationToken));
        Assert.True(await log.IsRunningAsync(TestContext.Current.CancellationToken));
    }
}
