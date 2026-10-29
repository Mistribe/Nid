using Microsoft.Extensions.DependencyInjection;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using Nid.TestSupport;
using NodaTime;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>Rental spec §3: the stored IRL series and the collection's run log.</summary>
public class IrlStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly LocalDate Day = new(2026, 10, 23);

    private static AsyncServiceScope Scope(NidApp app)
    {
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }

    [Fact]
    public async Task Values_list_by_quarter_and_an_upsert_replaces_a_manual_value()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<IIrlStore>();

        await store.UpsertAsync(new IrlValue(new IrlQuarter(2026, 2), 148.37m, new LocalDate(2026, 7, 12), Day, "insee"), default);
        await store.UpsertAsync(new IrlValue(new IrlQuarter(2025, 4), 145.77m, null, Day, "manual"), default);
        await store.UpsertAsync(new IrlValue(new IrlQuarter(2026, 1), 146.68m, new LocalDate(2026, 4, 10), Day, "insee"), default);
        Assert.Equal(["2025-Q4", "2026-Q1", "2026-Q2"], (await store.ListAsync(default)).Select(v => v.Quarter.ToString()));

        var insee = new IrlValue(new IrlQuarter(2025, 4), 145.78m, new LocalDate(2026, 1, 15), Day, "insee");
        await store.UpsertAsync(insee, default);
        var all = await store.ListAsync(default);
        Assert.Equal(3, all.Count);
        Assert.Equal(insee, all[0]);
    }

    [Fact]
    public async Task A_manual_upsert_writes_unless_the_quarter_holds_an_insee_value()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<IIrlStore>();
        var q = new IrlQuarter(2026, 2);

        Assert.True(await store.UpsertManualAsync(new IrlValue(q, 148m, null, Day, "manual"), default));
        Assert.True(await store.UpsertManualAsync(new IrlValue(q, 148.1m, null, Day, "manual"), default));
        await store.UpsertAsync(new IrlValue(q, 148.37m, new LocalDate(2026, 7, 12), Day, "insee"), default);
        Assert.False(await store.UpsertManualAsync(new IrlValue(q, 150m, null, Day, "manual"), default));

        Assert.Equal(("insee", 148.37m), (await store.ListAsync(default)).Select(v => (v.Source, v.Value)).Single());
    }

    [Fact]
    public async Task The_run_log_starts_one_run_at_a_time_and_lists_the_latest_first()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var log = scope.ServiceProvider.GetRequiredService<IIrlRunLog>();

        var first = await log.TryStartAsync(default);
        Assert.NotNull(first);
        Assert.Null(await log.TryStartAsync(default));
        Assert.True(await log.IsRunningAsync(default));

        await log.FinishAsync(first with { Status = "succeeded", FinishedAt = first.StartedAt, Added = 2, Replaced = 1, Rejected = 3 }, default);
        Assert.False(await log.IsRunningAsync(default));
        var second = await log.TryStartAsync(default);
        Assert.NotNull(second);

        var recent = await log.RecentAsync(5, default);
        Assert.Equal([second.Id, first.Id], recent.Select(r => r.Id));
        Assert.Equal(("succeeded", 2, 1, 3), (recent[1].Status, recent[1].Added, recent[1].Replaced, recent[1].Rejected));
        Assert.Equal(0, await log.PurgeBeforeAsync(Instant.FromUtc(2000, 1, 1, 0, 0), default));
    }
}
