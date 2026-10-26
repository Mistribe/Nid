using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Platform.Infrastructure.Persistence;
using Nid.Platform.Notifications;
using NodaTime;

namespace Nid.Platform.Infrastructure.Notifications;

/// <summary>Notifications design §2.3 and §4 on <see cref="PlatformDbContext"/>.</summary>
public sealed partial class NotificationStore(PlatformDbContext db, IClock clock) : INotificationStore
{
    private const int HistoryDays = 90;

    [JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    private sealed partial class ParamsJson : JsonSerializerContext;

    public async Task<IReadOnlyList<StoredNotification>> ForSourceAsync(PersonId person, IReadOnlyCollection<string> kinds, CancellationToken ct)
    {
        var wanted = kinds.ToArray();
        return await db.Notifications.AsNoTracking()
            .Where(n => n.PersonId == person.Value && wanted.Contains(n.Kind))
            .Select(n => new StoredNotification(n.Id, n.Key, n.State, n.NotifyOn, n.SnoozedUntil, n.ResolvedAt != null))
            .ToListAsync(ct);
    }

    public async Task ApplyAsync(PersonId person, IReadOnlyList<LifecycleChange> changes, CancellationToken ct)
    {
        // The single-row commands write in SQL: a notification tracked by an earlier call would be stale. Only those are
        // detached; the context is shared (audit entries), its tracker is not ours to clear.
        foreach (var entry in db.ChangeTracker.Entries<NotificationRow>().ToList())
            entry.State = EntityState.Detached;
        var now = clock.GetCurrentInstant();

        // Inserts go first and in SQL: a row another evaluation created meanwhile makes the insert a no-op, not a failure.
        foreach (var insert in changes.OfType<LifecycleChange.Insert>())
            await InsertAsync(person, insert, now, ct);

        var ids = changes.Where(c => c is not LifecycleChange.Insert).Select(IdOf).ToArray();
        if (ids.Length > 0)
        {
            var rows = await db.Notifications.Where(n => n.PersonId == person.Value && ids.Contains(n.Id)).ToDictionaryAsync(n => n.Id, ct);
            foreach (var change in changes)
            {
                if (change is LifecycleChange.Insert || !rows.TryGetValue(IdOf(change), out var row))
                    continue;
                Apply(row, change, now);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static Guid IdOf(LifecycleChange change) => change switch
    {
        LifecycleChange.Retime c => c.Id,
        LifecycleChange.OpenNow c => c.Id,
        LifecycleChange.Resolve c => c.Id,
        LifecycleChange.Wake c => c.Id,
        LifecycleChange.Reopen c => c.Id,
        LifecycleChange.SettleDismissed c => c.Id,
        LifecycleChange.Unsettle c => c.Id,
        _ => Guid.Empty,
    };

    private static void Apply(NotificationRow row, LifecycleChange change, Instant now)
    {
        switch (change)
        {
            case LifecycleChange.Retime c when row.State == NotificationStates.Pending:
                row.NotifyOn = c.NotifyOn;
                row.DueOn = c.Signal.DueOn;
                row.Params = JsonSerializer.Serialize(c.Signal.Params, ParamsJson.Default.IReadOnlyDictionaryStringString);
                row.ItemIds = c.Signal.ItemIds.Select(i => i.Value).ToArray();
                if (c.Open)
                    Open(row, now);
                break;
            case LifecycleChange.OpenNow when row.State == NotificationStates.Pending:
                Open(row, now);
                break;
            case LifecycleChange.Resolve when row.State is NotificationStates.Pending or NotificationStates.Open or NotificationStates.Snoozed:
                row.State = NotificationStates.Resolved;
                row.ResolvedAt = now;
                break;
            case LifecycleChange.Wake c when row.State == NotificationStates.Snoozed && row.SnoozedUntil is { } until && until <= c.Today:
                // Unread again: waking is news, so it sorts as new.
                Open(row, now);
                row.SnoozedUntil = null;
                row.ReadAt = null;
                break;
            case LifecycleChange.Reopen c when row.State == NotificationStates.Resolved:
                row.NotifyOn = c.NotifyOn;
                row.DueOn = c.Signal.DueOn;
                row.Params = JsonSerializer.Serialize(c.Signal.Params, ParamsJson.Default.IReadOnlyDictionaryStringString);
                row.ItemIds = c.Signal.ItemIds.Select(i => i.Value).ToArray();
                row.ResolvedAt = null;
                row.ReadAt = null;
                if (c.Open)
                    Open(row, now);
                else
                    row.State = NotificationStates.Pending;
                break;
            case LifecycleChange.SettleDismissed when row.State == NotificationStates.Dismissed && row.ResolvedAt is null:
                row.ResolvedAt = now;
                break;
            case LifecycleChange.Unsettle when row.State == NotificationStates.Dismissed:
                row.ResolvedAt = null;
                break;
        }
    }

    private static void Open(NotificationRow row, Instant now)
    {
        row.State = NotificationStates.Open;
        row.OpenedAt = now;
    }

    private Task<int> InsertAsync(PersonId person, LifecycleChange.Insert insert, Instant now, CancellationToken ct)
    {
        var signal = insert.Signal;
        var level = LevelText(insert.Kind.Level);
        var items = signal.ItemIds.Select(i => i.Value).ToArray();
        var state = insert.Open ? NotificationStates.Open : NotificationStates.Pending;
        var openedAt = insert.Open ? now : (Instant?)null;
        var json = JsonSerializer.Serialize(signal.Params, ParamsJson.Default.IReadOnlyDictionaryStringString);
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO platform.notification
                (id, person_id, kind, key, level, item_ids, due_on, notify_on, params, state, created_at, opened_at)
            VALUES
                ({Guid.CreateVersion7()}, {person.Value}, {insert.Kind.Kind}, {signal.Key}, {level}, {items}, {signal.DueOn}, {insert.NotifyOn},
                 CAST({json} AS jsonb), {state}, {now}, {openedAt})
            ON CONFLICT (person_id, key) DO NOTHING
            """, ct);
    }

    public async Task<LocalDate?> LastEvaluatedAsync(PersonId person, CancellationToken ct)
    {
        var days = await db.NotificationRuns.AsNoTracking().Where(r => r.PersonId == person.Value).Select(r => r.LastOkOn).ToListAsync(ct);
        return days.Count == 0 || days.Any(d => d is null) ? null : days.Min();
    }

    public Task RecordRunAsync(PersonId person, string source, LocalDate on, bool ok, CancellationToken ct)
    {
        var lastOk = ok ? on : (LocalDate?)null;
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO platform.notification_run (person_id, source, ran_on, ran_at, ok, last_ok_on)
            VALUES ({person.Value}, {source}, {on}, {clock.GetCurrentInstant()}, {ok}, {lastOk})
            ON CONFLICT (person_id, source) DO UPDATE SET
                ran_on = EXCLUDED.ran_on,
                ran_at = EXCLUDED.ran_at,
                ok = EXCLUDED.ok,
                last_ok_on = CASE WHEN EXCLUDED.ok THEN EXCLUDED.last_ok_on ELSE platform.notification_run.last_ok_on END
            """, ct);
    }

    public async Task<Instant?> LastRunAtAsync(PersonId person, CancellationToken ct)
        => await db.NotificationRuns.AsNoTracking().Where(r => r.PersonId == person.Value).MaxAsync(r => (Instant?)r.RanAt, ct);

    public async Task<IReadOnlyList<NotificationRecord>> ListAsync(
        PersonId person, NotificationList list, IReadOnlySet<ItemId> visible, LocalDate today, Guid? before, int limit, CancellationToken ct)
    {
        var query = Visible(db.Notifications.AsNoTracking().Where(n => n.PersonId == person.Value), visible);
        switch (list)
        {
            case NotificationList.Todo:
                query = query.Where(n => n.State == NotificationStates.Open);
                break;
            case NotificationList.Snoozed:
                query = query.Where(n => n.State == NotificationStates.Snoozed);
                break;
            default:
                var since = today.PlusDays(-HistoryDays).AtMidnight().InUtc().ToInstant();
                query = query.Where(n => (n.State == NotificationStates.Resolved || n.State == NotificationStates.Dismissed)
                    && (n.ResolvedAt ?? n.DismissedAt) >= since);
                break;
        }

        // Newest first by when the row opened (its creation when it never did), then by id; "before" is a keyset on that pair.
        if (before is { } last)
        {
            var anchor = await db.Notifications.AsNoTracking().Where(n => n.PersonId == person.Value && n.Id == last)
                .Select(n => (Instant?)(n.OpenedAt ?? n.CreatedAt)).FirstOrDefaultAsync(ct);
            if (anchor is not { } at)
                return [];
            query = query.Where(n => (n.OpenedAt ?? n.CreatedAt) < at || ((n.OpenedAt ?? n.CreatedAt) == at && n.Id.CompareTo(last) > 0));
        }

        var rows = await query.OrderByDescending(n => n.OpenedAt ?? n.CreatedAt).ThenBy(n => n.Id).Take(limit).ToListAsync(ct);
        return rows.Select(ToRecord).ToList();
    }

    public async Task<(int Unread, int CriticalUnread)> CountAsync(PersonId person, IReadOnlySet<ItemId> visible, CancellationToken ct)
    {
        var unread = Visible(db.Notifications.AsNoTracking().Where(n => n.PersonId == person.Value), visible)
            .Where(n => n.State == NotificationStates.Open && n.ReadAt == null);
        var critical = LevelText(NotificationLevel.Critical);
        var levels = await unread.GroupBy(n => n.Level).Select(g => new { Level = g.Key, Count = g.Count() }).ToListAsync(ct);
        return (levels.Sum(l => l.Count), levels.Where(l => l.Level == critical).Sum(l => l.Count));
    }

    public async Task<NotificationRecord?> FindAsync(PersonId person, Guid id, CancellationToken ct)
    {
        var row = await db.Notifications.AsNoTracking().FirstOrDefaultAsync(n => n.PersonId == person.Value && n.Id == id, ct);
        return row is null ? null : ToRecord(row);
    }

    public async Task MarkReadAsync(PersonId person, IReadOnlyCollection<Guid> ids, Instant at, CancellationToken ct)
    {
        var wanted = ids.ToArray();
        await db.Notifications.Where(n => n.PersonId == person.Value && wanted.Contains(n.Id) && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, at), ct);
    }

    public async Task SnoozeAsync(Guid id, LocalDate until, CancellationToken ct)
        => await db.Notifications.Where(n => n.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.State, NotificationStates.Snoozed).SetProperty(n => n.SnoozedUntil, until), ct);

    public async Task DismissAsync(Guid id, Instant at, CancellationToken ct)
        => await db.Notifications.Where(n => n.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.State, NotificationStates.Dismissed).SetProperty(n => n.DismissedAt, at), ct);

    public async Task<int> PurgeAsync(Instant olderThan, CancellationToken ct)
        => await db.Notifications
            .Where(n => (n.State == NotificationStates.Resolved || n.State == NotificationStates.Dismissed) && n.ResolvedAt < olderThan)
            .ExecuteDeleteAsync(ct);

    /// <summary>A row shows only when every item it concerns is visible: <c>item_ids &lt;@ visible</c>.</summary>
    private static IQueryable<NotificationRow> Visible(IQueryable<NotificationRow> query, IReadOnlySet<ItemId> visible)
    {
        var allowed = visible.Select(i => i.Value).ToArray();
        return query.Where(n => n.ItemIds.All(i => allowed.Contains(i)));
    }

    private static string LevelText(NotificationLevel level) => level.ToString().ToLowerInvariant();

    private static NotificationRecord ToRecord(NotificationRow row) => new(
        row.Id, new PersonId(row.PersonId), row.Kind, row.Key, Enum.Parse<NotificationLevel>(row.Level, ignoreCase: true),
        row.ItemIds.Select(i => new ItemId(i)).ToList(), row.DueOn, row.NotifyOn,
        JsonSerializer.Deserialize(row.Params, ParamsJson.Default.DictionaryStringString) ?? [],
        row.State, row.ReadAt, row.SnoozedUntil, row.CreatedAt, row.OpenedAt, row.ResolvedAt);
}
