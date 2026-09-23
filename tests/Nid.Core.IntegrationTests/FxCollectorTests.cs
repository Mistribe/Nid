using Nid.Core.Application.Fx;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Core.IntegrationTests;

[Collection("postgres")]
public class FxCollectorTests(PostgresFixture postgres)
{
    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    // Three days after SubTracker's first file, so a backfill walks a short, known range.
    private static readonly FxOptions Options = new();
    private static readonly LocalDate Start = Options.SubTrackerStart;
    private static readonly LocalDate Today = Start.PlusDays(2);

    private static (FxCollector Collector, FxRateStore Store, FxRunLog Log) Build(CoreDbContext db, FakeFxSources sources)
    {
        var clock = new FixedClock(Today.At(new LocalTime(7, 0)).InUtc().ToInstant());
        var store = new FxRateStore(db);
        var log = new FxRunLog(db, clock);
        return (new FxCollector(sources, store, log, Options, clock), store, log);
    }

    private static async Task<decimal> RateAsync(FxRateStore store, string quote, LocalDate on)
        => Assert.Single(await store.ReadAsync([Currency.TryCreate(quote, out var c) ? c : default], on, on, default)).Rate;

    [Fact]
    public async Task The_backfill_writes_the_ECB_first_then_SubTracker_over_it()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var sources = new FakeFxSources { Ecb = FxFixtures.EcbZip((Start, 1.25m, 0.75m)) };
        sources.Days[Start] = FxFixtures.SubTrackerDay(("EUR", 0.81m), ("GBP", 0.61m));
        var (collector, store, _) = Build(db, sources);

        var run = (await collector.BackfillAsync(default)).Ok();

        Assert.Equal((FxRunStatus.Succeeded, 2), (run.Status, run.FilesRead));
        Assert.Equal(0.81m, await RateAsync(store, "EUR", Start));   // SubTracker replaced the ECB's 0.8
        Assert.Equal(["ecb", "2025-07-30", "2025-07-31", "2025-08-01"], sources.Requested);
    }

    [Fact]
    public async Task A_second_backfill_fetches_no_day_already_stored()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var sources = new FakeFxSources { Ecb = FxFixtures.EcbZip((Start, 1.25m, 0.75m)) };
        sources.Days[Start] = FxFixtures.SubTrackerDay(("EUR", 0.81m));
        var (collector, store, _) = Build(db, sources);
        await collector.BackfillAsync(default);
        sources.Requested.Clear();

        (await collector.BackfillAsync(default)).Ok();

        Assert.Equal(["ecb", "2025-07-31", "2025-08-01"], sources.Requested); // Start already holds SubTracker rates
        Assert.Equal(0.81m, await RateAsync(store, "EUR", Start));
    }

    [Fact]
    public async Task The_daily_run_starts_the_day_after_the_latest_SubTracker_rate()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var sources = new FakeFxSources();
        sources.Days[Start] = FxFixtures.SubTrackerDay(("EUR", 0.8m));
        sources.Days[Today] = FxFixtures.SubTrackerDay(("EUR", 0.82m));
        var (collector, store, _) = Build(db, sources);
        await new FxRateStore(db).WriteAsync([new FxRate(Start, Currency.Eur, 0.8m, FxRateSource.SubTrackerData)], default);

        var run = (await collector.CollectDailyAsync(default)).Ok();

        Assert.Equal(["2025-07-31", "2025-08-01"], sources.Requested);
        Assert.Equal((FxRunKind.Daily, FxRunStatus.Succeeded, 1), (run.Kind, run.Status, run.FilesRead));
        Assert.Equal(0.82m, await RateAsync(store, "EUR", Today));
    }

    [Fact]
    public async Task An_implausible_rate_is_listed_and_the_rest_of_the_file_written()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var sources = new FakeFxSources();
        sources.Days[Today] = FxFixtures.SubTrackerDay(("EUR", 0.8m), ("GBP", 2m));
        var (collector, store, _) = Build(db, sources);
        await store.WriteAsync([new FxRate(Today.PlusDays(-1), Currency.TryCreate("GBP", out var gbp) ? gbp : default, 0.7m, FxRateSource.Ecb)], default);

        var run = (await collector.CollectDailyAsync(default)).Ok();

        var rejected = Assert.Single(run.Rejected);
        Assert.Equal(("GBP", 0.7m, FxCodes.ImplausibleChange), (rejected.Quote, rejected.Previous, rejected.Reason));
        Assert.Equal(0.8m, await RateAsync(store, "EUR", Today));
    }

    [Fact]
    public async Task A_source_failure_ends_the_run_failed_and_recorded()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var (collector, _, log) = Build(db, FakeFxSources.Offline());

        var run = (await collector.BackfillAsync(default)).Ok();

        Assert.Equal((FxRunStatus.Failed, FxCodes.SourceUnavailable), (run.Status, run.FailureCode));
        Assert.Equal(run, Assert.Single(await log.RecentAsync(5, default)));
        Assert.False(await log.IsRunningAsync(default));
    }

    [Fact]
    public async Task A_malformed_ECB_file_writes_nothing()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var (collector, store, _) = Build(db, new FakeFxSources { Ecb = "not a zip"u8.ToArray() });

        var run = (await collector.BackfillAsync(default)).Ok();

        Assert.Equal(FxCodes.SourceMalformed, run.FailureCode);
        Assert.True(await store.IsEmptyAsync(default));
    }

    [Fact]
    public async Task A_run_while_another_is_running_is_refused()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var (collector, _, log) = Build(db, new FakeFxSources());
        await log.TryStartAsync(FxRunKind.Backfill, default);

        var refused = await collector.CollectDailyAsync(default);

        Assert.True(refused.TryGetFailure(out var failure));
        Assert.Equal(FxCodes.SyncRunning, failure!.Code);
    }
}
