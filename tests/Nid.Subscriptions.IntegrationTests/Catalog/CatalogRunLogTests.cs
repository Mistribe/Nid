using System.Text.Json;
using System.Text.Json.Nodes;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;
using Duration = NodaTime.Duration;

namespace Nid.Subscriptions.IntegrationTests.Catalog;

[Collection("postgres")]
public class CatalogRunLogTests(PostgresFixture fixture)
{
    private const string Commit = "df0146a8f3532f5c790aa54a34d4871e96b62a95";

    private sealed class TestClock(Instant now) : IClock
    {
        public Instant Now { get; set; } = now;
        public Instant GetCurrentInstant() => Now;
    }

    private static readonly Instant Start = Instant.FromUtc(2026, 9, 24, 7, 0);

    private async Task<(CatalogRunLog Log, TestClock Clock)> NewLogAsync()
    {
        var clock = new TestClock(Start);
        return (new CatalogRunLog(PostgresFixture.NewSubscriptionsContext(await fixture.NewConnectionStringAsync()), clock), clock);
    }

    [Fact]
    public async Task Only_one_run_at_a_time()
    {
        var (log, _) = await NewLogAsync();

        var first = await log.TryStartAsync(Commit, default);
        Assert.NotNull(first);
        Assert.Equal((CatalogRunStatus.Running, Commit), (first.Status, first.Commit));
        Assert.True(await log.IsRunningAsync(default));
        Assert.Null(await log.TryStartAsync(Commit, default));

        await log.FinishAsync(first with { Status = CatalogRunStatus.Succeeded, FinishedAt = Start, Report = CatalogReport.Empty }, default);
        Assert.False(await log.IsRunningAsync(default));
        Assert.NotNull(await log.TryStartAsync(Commit, default));
    }

    [Fact]
    public async Task A_run_left_running_for_an_hour_is_closed_as_abandoned()
    {
        var (log, clock) = await NewLogAsync();
        await log.TryStartAsync(Commit, default);

        clock.Now = Start + Duration.FromMinutes(61);
        Assert.NotNull(await log.TryStartAsync(Commit, default));

        var abandoned = Assert.Single(await log.RecentAsync(10, default), r => r.StartedAt == Start);
        Assert.Equal((CatalogRunStatus.Failed, CatalogCodes.RunAbandoned), (abandoned.Status, abandoned.FailureCode));
    }

    /// <summary>Final review F1: a run left running by a crash no longer blocks a request once it is an hour old.</summary>
    [Fact]
    public async Task A_run_left_running_for_an_hour_no_longer_counts_as_running()
    {
        var (log, clock) = await NewLogAsync();
        Assert.NotNull(await log.TryStartAsync(Commit, default));

        clock.Now = Start + Duration.FromMinutes(59);
        Assert.True(await log.IsRunningAsync(default));

        clock.Now = Start + Duration.FromMinutes(61);
        Assert.False(await log.IsRunningAsync(default));
        var abandoned = Assert.Single(await log.RecentAsync(10, default));
        Assert.Equal((CatalogRunStatus.Failed, CatalogCodes.RunAbandoned, (Instant?)clock.Now),
            (abandoned.Status, abandoned.FailureCode, abandoned.FinishedAt));
    }

    [Fact]
    public async Task The_report_round_trips()
    {
        var (log, _) = await NewLogAsync();
        var run = (await log.TryStartAsync(Commit, default))!;
        var report = CatalogReport.Empty with
        {
            ProvidersAdded = ["netflix", "spotify"],
            AdoptedRemaining = ["wow"],
            LabelsCreated = ["home"],
            Duplicates = [new CatalogDuplicate("labels", "hosting")],
            Refused = [new CatalogRefusal("providers", 3, "Netflix", CatalogCodes.InvalidKey)],
        };
        await log.FinishAsync(run with { Status = CatalogRunStatus.Succeeded, FinishedAt = Start, Report = report }, default);

        var read = Assert.Single(await log.RecentAsync(10, default)).Report!;
        Assert.Equal(["netflix", "spotify"], read.ProvidersAdded);
        Assert.Equal(["wow"], read.AdoptedRemaining);
        Assert.Equal(["home"], read.LabelsCreated);
        Assert.Equal([new CatalogDuplicate("labels", "hosting")], read.Duplicates);
        Assert.Equal([new CatalogRefusal("providers", 3, "Netflix", CatalogCodes.InvalidKey)], read.Refused);
        Assert.Empty(read.ProvidersArchived);
    }

    [Fact]
    public async Task The_purge_keeps_the_latest_succeeded_run()
    {
        var (log, clock) = await NewLogAsync();
        foreach (var status in new[] { CatalogRunStatus.Succeeded, CatalogRunStatus.Succeeded, CatalogRunStatus.Failed })
        {
            var run = (await log.TryStartAsync(Commit, default))!;
            await log.FinishAsync(run with { Status = status, FinishedAt = clock.Now }, default);
            clock.Now += Duration.FromMinutes(1);
        }

        Assert.Equal(2, await log.PurgeBeforeAsync(clock.Now, default));

        var kept = Assert.Single(await log.RecentAsync(10, default));
        Assert.Equal((CatalogRunStatus.Succeeded, Start + Duration.FromMinutes(1)), (kept.Status, kept.StartedAt));
        Assert.True(await log.HasSucceededAsync(default));
    }

    [Fact]
    public async Task The_icon_section_round_trips_and_a_report_stored_before_it_reads_without_icons()
    {
        var (log, _) = await NewLogAsync();
        var run = (await log.TryStartAsync(Commit, default))!;
        var report = CatalogReport.Empty with
        {
            Icons = new IconReport(["netflix"], 14, [new IconRefusal("nounou-top", IconCodes.UnsupportedFormat)]),
        };
        await log.FinishAsync(run with { Status = CatalogRunStatus.Succeeded, FinishedAt = Start, Report = report }, default);

        var read = Assert.Single(await log.RecentAsync(10, default)).Report!.Icons;
        Assert.Equal(["netflix"], read.Fetched);
        Assert.Equal(14, read.Unchanged);
        Assert.Equal([new IconRefusal("nounou-top", IconCodes.UnsupportedFormat)], read.Refused);

        var connectionString = await fixture.NewConnectionStringAsync();
        var older = JsonSerializer.SerializeToNode(CatalogReport.Empty with { ProvidersAdded = ["netflix"] }, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        Assert.True(older.Remove("icons"));
        await using (var seed = PostgresFixture.NewSubscriptionsContext(connectionString))
        {
            seed.CatalogSyncRuns.Add(new CatalogSyncRunRow
            {
                Id = Guid.CreateVersion7(), StartedAt = Start, FinishedAt = Start, Status = "succeeded", Commit = Commit,
                Report = new JsonObject { ["report"] = older, ["usedBy"] = new JsonObject() }.ToJsonString(),
            });
            await seed.SaveChangesAsync();
        }

        var stored = Assert.Single(await new CatalogRunLog(PostgresFixture.NewSubscriptionsContext(connectionString), new TestClock(Start)).RecentAsync(10, default));
        Assert.Equal(["netflix"], stored.Report!.ProvidersAdded);
        Assert.Empty(stored.Report.Icons.Fetched);
        Assert.Equal(0, stored.Report.Icons.Unchanged);
    }
}
