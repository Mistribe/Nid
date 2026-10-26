using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Platform.Infrastructure.Persistence;
using Nid.Platform.Notifications;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests.Notifications;

/// <summary>Notifications design §2.3 and §4: the tables and the store.</summary>
public class NotificationStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly LocalDate Today = new(2025, 10, 15);
    private static readonly Instant Now = Instant.FromUtc(2025, 10, 15, 0, 0);
    private static readonly NotificationKind Kind = new("test_kind", "test", NotificationLevel.Action, "test", null);

    private static Signal SignalOf(string key, params ItemId[] items)
        => new(key, Kind.Kind, items, Today.PlusDays(3), new Dictionary<string, string> { ["name"] = "Netflix" });

    private static async Task InsertAsync(INotificationStore store, PersonId person, Signal signal, bool open = true)
        => await store.ApplyAsync(person, [new LifecycleChange.Insert(signal, Kind, Today, open)], default);

    private static async Task<Guid> IdOfAsync(NidApp app, PersonId person, string key)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        return await db.Notifications.AsNoTracking().Where(n => n.PersonId == person.Value && n.Key == key).Select(n => n.Id).SingleAsync();
    }

    private static async Task SetAsync(NidApp app, Guid id, Action<NotificationRow> change)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var row = await db.Notifications.SingleAsync(n => n.Id == id);
        change(row);
        await db.SaveChangesAsync();
    }

    private static async Task<IReadOnlyList<Guid>> ListAsync(NidApp app, PersonId person, NotificationList list, params ItemId[] visible)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INotificationStore>();
        return (await store.ListAsync(person, list, visible.ToHashSet(), Today, null, 50, default)).Select(n => n.Id).ToList();
    }

    [Fact]
    public async Task Insert_twice_keeps_one_row()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = new ItemId(Guid.NewGuid());
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INotificationStore>();

        await InsertAsync(store, app.AdultA, SignalOf("k:1", item));
        await InsertAsync(store, app.AdultA, SignalOf("k:1", item));

        var stored = await store.ForSourceAsync(app.AdultA, [Kind.Kind], default);
        var row = Assert.Single(stored);
        Assert.Equal(("k:1", NotificationStates.Open), (row.Key, row.State));
        var record = await store.FindAsync(app.AdultA, row.Id, default);
        Assert.Equal(("Netflix", NotificationLevel.Action, (Instant?)Now), (record!.Params["name"], record.Level, record.OpenedAt));
        Assert.Equal([item], record.ItemIds);
    }

    [Fact]
    public async Task Lists_follow_the_states()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = new ItemId(Guid.NewGuid());
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<INotificationStore>();
            foreach (var key in new[] { "open", "snoozed", "resolved", "dismissed", "pending" })
                await InsertAsync(store, app.AdultA, SignalOf(key, item), open: key != "pending");
            await store.SnoozeAsync(await IdOfAsync(app, app.AdultA, "snoozed"), Today.PlusDays(7), default);
            await store.ApplyAsync(app.AdultA, [new LifecycleChange.Resolve(await IdOfAsync(app, app.AdultA, "resolved"))], default);
            await store.DismissAsync(await IdOfAsync(app, app.AdultA, "dismissed"), Now, default);
        }

        Assert.Equal([await IdOfAsync(app, app.AdultA, "open")], await ListAsync(app, app.AdultA, NotificationList.Todo, item));
        Assert.Equal([await IdOfAsync(app, app.AdultA, "snoozed")], await ListAsync(app, app.AdultA, NotificationList.Snoozed, item));
        var history = await ListAsync(app, app.AdultA, NotificationList.History, item);
        Assert.Equal(
            new[] { await IdOfAsync(app, app.AdultA, "resolved"), await IdOfAsync(app, app.AdultA, "dismissed") }.Order(),
            history.Order());
        Assert.Empty(await ListAsync(app, app.AdultB, NotificationList.Todo, item));
    }

    [Fact]
    public async Task History_keeps_90_days()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = new ItemId(Guid.NewGuid());
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<INotificationStore>();
            await InsertAsync(store, app.AdultA, SignalOf("old", item));
            await InsertAsync(store, app.AdultA, SignalOf("recent", item));
        }

        var old = await IdOfAsync(app, app.AdultA, "old");
        var recent = await IdOfAsync(app, app.AdultA, "recent");
        await SetAsync(app, old, n => { n.State = NotificationStates.Resolved; n.ResolvedAt = Now - NodaTime.Duration.FromDays(91); });
        await SetAsync(app, recent, n => { n.State = NotificationStates.Resolved; n.ResolvedAt = Now - NodaTime.Duration.FromDays(89); });

        Assert.Equal([recent], await ListAsync(app, app.AdultA, NotificationList.History, item));

        await using var purge = app.Services.CreateAsyncScope();
        var purged = await purge.ServiceProvider.GetRequiredService<INotificationStore>().PurgeAsync(Now - NodaTime.Duration.FromDays(90), default);
        Assert.Equal(1, purged);
        var db = purge.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.Equal([recent], await db.Notifications.AsNoTracking().Select(n => n.Id).ToListAsync());
    }

    [Fact]
    public async Task Visibility_filter_is_a_subset_test()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var seen = new ItemId(Guid.NewGuid());
        var hidden = new ItemId(Guid.NewGuid());
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<INotificationStore>();
            await InsertAsync(store, app.AdultA, SignalOf("both", seen, hidden));
            await InsertAsync(store, app.AdultA, SignalOf("one", seen));
        }

        var one = await IdOfAsync(app, app.AdultA, "one");
        Assert.Equal([one], await ListAsync(app, app.AdultA, NotificationList.Todo, seen));
        await using var counting = app.Services.CreateAsyncScope();
        var counter = counting.ServiceProvider.GetRequiredService<INotificationStore>();
        Assert.Equal((1, 0), await counter.CountAsync(app.AdultA, new HashSet<ItemId> { seen }, default));
        Assert.Equal((2, 0), await counter.CountAsync(app.AdultA, new HashSet<ItemId> { seen, hidden }, default));
    }

    [Fact]
    public async Task Count_is_open_and_unread()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = new ItemId(Guid.NewGuid());
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INotificationStore>();
        foreach (var key in new[] { "unread", "read", "snoozed" })
            await InsertAsync(store, app.AdultA, SignalOf(key, item));
        await store.MarkReadAsync(app.AdultA, [await IdOfAsync(app, app.AdultA, "read")], Now, default);
        await store.SnoozeAsync(await IdOfAsync(app, app.AdultA, "snoozed"), Today.PlusDays(3), default);

        Assert.Equal((1, 0), await store.CountAsync(app.AdultA, new HashSet<ItemId> { item }, default));
    }

    [Fact]
    public async Task Retime_open_now_and_wake_move_the_row()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = new ItemId(Guid.NewGuid());
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INotificationStore>();
        await InsertAsync(store, app.AdultA, SignalOf("a", item), open: false);
        var id = await IdOfAsync(app, app.AdultA, "a");

        var moved = SignalOf("a", item) with { DueOn = Today.PlusDays(20) };
        await store.ApplyAsync(app.AdultA, [new LifecycleChange.Retime(id, Today.PlusDays(10), false, moved)], default);
        var row = (await store.FindAsync(app.AdultA, id, default))!;
        Assert.Equal((NotificationStates.Pending, Today.PlusDays(10), (LocalDate?)Today.PlusDays(20)), (row.State, row.NotifyOn, row.DueOn));

        await store.ApplyAsync(app.AdultA, [new LifecycleChange.OpenNow(id)], default);
        row = (await store.FindAsync(app.AdultA, id, default))!;
        Assert.Equal((NotificationStates.Open, (Instant?)Now), (row.State, row.OpenedAt));

        await store.MarkReadAsync(app.AdultA, [id], Now, default);
        await store.SnoozeAsync(id, Today.PlusDays(2), default);
        await store.ApplyAsync(app.AdultA, [new LifecycleChange.Wake(id, Today.PlusDays(1))], default);
        Assert.Equal(NotificationStates.Snoozed, (await store.FindAsync(app.AdultA, id, default))!.State); // not yet
        await store.ApplyAsync(app.AdultA, [new LifecycleChange.Wake(id, Today.PlusDays(2))], default);
        row = (await store.FindAsync(app.AdultA, id, default))!;
        Assert.Equal((NotificationStates.Open, (LocalDate?)null, (Instant?)null, (Instant?)Now), (row.State, row.SnoozedUntil, row.ReadAt, row.OpenedAt));
    }

    [Fact]
    public async Task Paging_with_before_returns_every_row_once_in_order()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = new ItemId(Guid.NewGuid());
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INotificationStore>();
        foreach (var key in new[] { "a", "b", "c", "d", "e" }) // all opened at the same instant
            await InsertAsync(store, app.AdultA, SignalOf(key, item));
        await InsertAsync(store, app.AdultB, SignalOf("other", item));
        var everything = (await store.ListAsync(app.AdultA, NotificationList.Todo, new HashSet<ItemId> { item }, Today, null, 50, default)).Select(n => n.Id).ToList();
        Assert.Equal(5, everything.Count);

        var paged = new List<Guid>();
        Guid? before = null;
        while (true)
        {
            var page = await store.ListAsync(app.AdultA, NotificationList.Todo, new HashSet<ItemId> { item }, Today, before, 2, default);
            if (page.Count == 0)
                break;
            paged.AddRange(page.Select(n => n.Id));
            before = page[^1].Id;
        }

        Assert.Equal(everything, paged);

        // A stale id, or another person's row, is an empty page, not the first page again.
        var set = new HashSet<ItemId> { item };
        Assert.Empty(await store.ListAsync(app.AdultA, NotificationList.Todo, set, Today, Guid.NewGuid(), 2, default));
        var others = await IdOfAsync(app, app.AdultB, "other");
        Assert.Empty(await store.ListAsync(app.AdultA, NotificationList.Todo, set, Today, others, 2, default));
    }

    [Fact]
    public async Task Count_separates_critical_and_ignores_read()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = new ItemId(Guid.NewGuid());
        var critical = new NotificationKind("crit_kind", "test", NotificationLevel.Critical, "test", null);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INotificationStore>();
        await store.ApplyAsync(app.AdultA,
        [
            new LifecycleChange.Insert(SignalOf("c1", item) with { Kind = critical.Kind }, critical, Today, true),
            new LifecycleChange.Insert(SignalOf("c2", item) with { Kind = critical.Kind }, critical, Today, true),
        ], default);
        await InsertAsync(store, app.AdultA, SignalOf("info", item));
        await store.MarkReadAsync(app.AdultA, [await IdOfAsync(app, app.AdultA, "c2")], Now, default);

        Assert.Equal((2, 1), await store.CountAsync(app.AdultA, new HashSet<ItemId> { item }, default));
    }

    [Fact]
    public async Task Retime_with_open_opens_and_refreshes_the_row()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = new ItemId(Guid.NewGuid());
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INotificationStore>();
        await InsertAsync(store, app.AdultA, SignalOf("a", item), open: false);
        var id = await IdOfAsync(app, app.AdultA, "a");
        var other = new ItemId(Guid.NewGuid());
        var moved = new Signal("a", Kind.Kind, [item, other], Today.PlusDays(1), new Dictionary<string, string> { ["name"] = "Disney" });

        await store.ApplyAsync(app.AdultA, [new LifecycleChange.Retime(id, Today, true, moved)], default);

        var row = (await store.FindAsync(app.AdultA, id, default))!;
        Assert.Equal((NotificationStates.Open, (Instant?)Now, (LocalDate?)Today.PlusDays(1), "Disney"), (row.State, row.OpenedAt, row.DueOn, row.Params["name"]));
        Assert.Equal([item, other], row.ItemIds);
    }

    [Fact]
    public async Task Resolve_applies_to_pending_and_open_rows()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = new ItemId(Guid.NewGuid());
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INotificationStore>();
        await InsertAsync(store, app.AdultA, SignalOf("p", item), open: false);
        await InsertAsync(store, app.AdultA, SignalOf("o", item));
        var p = await IdOfAsync(app, app.AdultA, "p");
        var o = await IdOfAsync(app, app.AdultA, "o");

        await store.ApplyAsync(app.AdultA, [new LifecycleChange.Resolve(p), new LifecycleChange.Resolve(o)], default);

        foreach (var id in new[] { p, o })
        {
            var row = (await store.FindAsync(app.AdultA, id, default))!;
            Assert.Equal((NotificationStates.Resolved, (Instant?)Now), (row.State, row.ResolvedAt));
        }
    }

    [Fact]
    public async Task Runs_are_recorded_per_source()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INotificationStore>();
        Assert.Null(await store.LastEvaluatedAsync(app.AdultA, default));

        await store.RecordRunAsync(app.AdultA, "subscriptions", Today, true, default);
        await store.RecordRunAsync(app.AdultA, "income", Today.PlusDays(-1), true, default);
        Assert.Equal(Today.PlusDays(-1), await store.LastEvaluatedAsync(app.AdultA, default));

        await store.RecordRunAsync(app.AdultA, "income", Today, false, default); // a failure does not advance the day
        Assert.Equal(Today.PlusDays(-1), await store.LastEvaluatedAsync(app.AdultA, default));
        Assert.Equal(Now, await store.LastRunAtAsync(app.AdultA, default));
    }
}
