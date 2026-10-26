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

    /// <summary>The published critical kinds: no source signals them again, so reading one is what ends it (and starts its 90 days).</summary>
    private static readonly string[] EndsWhenRead =
        PlatformNotificationKinds.All.Where(k => k.Level == NotificationLevel.Critical).Select(k => k.Kind).ToArray();

    [JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(IReadOnlyDictionary<string, int>))]
    [JsonSerializable(typeof(Dictionary<string, int>))]
    private sealed partial class StoreJson : JsonSerializerContext;

    public async Task<IReadOnlyList<StoredNotification>> ForSourceAsync(PersonId person, IReadOnlyCollection<string> kinds, CancellationToken ct)
    {
        var wanted = kinds.ToArray();
        return await db.Notifications.AsNoTracking()
            .Where(n => n.PersonId == person.Value && wanted.Contains(n.Kind))
            .Select(n => new StoredNotification(n.Id, n.Key, n.State, n.NotifyOn, n.SnoozedUntil, n.ResolvedAt != null))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<OpenedNotification>> ApplyAsync(PersonId person, IReadOnlyList<LifecycleChange> changes, CancellationToken ct)
    {
        var opened = new List<OpenedNotification>();
        // The single-row commands write in SQL: a notification tracked by an earlier call would be stale. Only those are
        // detached; the context is shared (audit entries), its tracker is not ours to clear.
        foreach (var entry in db.ChangeTracker.Entries<NotificationRow>().ToList())
            entry.State = EntityState.Detached;
        var now = clock.GetCurrentInstant();

        // Inserts go first and in SQL: a row another evaluation created meanwhile makes the insert a no-op, not a failure.
        foreach (var insert in changes.OfType<LifecycleChange.Insert>())
        {
            var id = Guid.CreateVersion7();
            if (await InsertAsync(person, id, insert, now, ct) == 1 && insert.Open)
                opened.Add(new OpenedNotification(id, insert.Kind.Level, 1, insert.Signal.Key, insert.Signal.ItemIds, insert.Kind.Kind, null));
        }

        var openedRows = new List<NotificationRow>();
        var ids = changes.Where(c => c is not LifecycleChange.Insert).Select(IdOf).ToArray();
        if (ids.Length > 0)
        {
            var rows = await db.Notifications.Where(n => n.PersonId == person.Value && ids.Contains(n.Id)).ToDictionaryAsync(n => n.Id, ct);
            foreach (var change in changes)
            {
                if (change is LifecycleChange.Insert || !rows.TryGetValue(IdOf(change), out var row))
                    continue;
                if (Apply(row, change, now))
                    openedRows.Add(row);
            }
        }

        await db.SaveChangesAsync(ct);
        opened.AddRange(openedRows.Select(r => new OpenedNotification(r.Id, Enum.Parse<NotificationLevel>(r.Level, ignoreCase: true), r.Round, r.Key, r.ItemIds.Select(i => new ItemId(i)).ToList(), r.Kind, null)));
        return opened;
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

    /// <summary>Applies one change to its row; true when the row opened.</summary>
    private static bool Apply(NotificationRow row, LifecycleChange change, Instant now)
    {
        switch (change)
        {
            case LifecycleChange.Retime c when row.State == NotificationStates.Pending:
                row.NotifyOn = c.NotifyOn;
                row.DueOn = c.Signal.DueOn;
                row.Params = JsonSerializer.Serialize(c.Signal.Params, StoreJson.Default.IReadOnlyDictionaryStringString);
                row.ItemIds = c.Signal.ItemIds.Select(i => i.Value).ToArray();
                if (c.Open)
                    Open(row, now);
                return c.Open;
            case LifecycleChange.OpenNow when row.State == NotificationStates.Pending:
                Open(row, now);
                return true;
            case LifecycleChange.Resolve when row.State is NotificationStates.Pending or NotificationStates.Open or NotificationStates.Snoozed:
                row.State = NotificationStates.Resolved;
                row.ResolvedAt = now;
                return false;
            case LifecycleChange.Wake c when row.State == NotificationStates.Snoozed && row.SnoozedUntil is { } until && until <= c.Today:
                // Unread again: waking is news, so it sorts as new.
                Open(row, now);
                row.SnoozedUntil = null;
                row.ReadAt = null;
                return true;
            case LifecycleChange.Reopen c when row.State == NotificationStates.Resolved:
                row.NotifyOn = c.NotifyOn;
                row.DueOn = c.Signal.DueOn;
                row.Params = JsonSerializer.Serialize(c.Signal.Params, StoreJson.Default.IReadOnlyDictionaryStringString);
                row.ItemIds = c.Signal.ItemIds.Select(i => i.Value).ToArray();
                row.ResolvedAt = null;
                row.ReadAt = null;
                if (c.Open)
                    Open(row, now);
                else
                    row.State = NotificationStates.Pending;
                return c.Open;
            case LifecycleChange.SettleDismissed when row.State == NotificationStates.Dismissed && row.ResolvedAt is null:
                row.ResolvedAt = now;
                return false;
            case LifecycleChange.Unsettle when row.State == NotificationStates.Dismissed:
                row.ResolvedAt = null;
                return false;
            default:
                return false;
        }
    }

    /// <summary>A row that opens after it was already open (a wake, a reopen of a resolved key) starts a new round.</summary>
    private static void Open(NotificationRow row, Instant now)
    {
        if (row.OpenedAt is not null)
        {
            row.Round++;
            // A new round is news again: it may go in a later digest (never in two digests is per round).
            row.DigestedAt = null;
            row.DigestId = null;
        }
        row.State = NotificationStates.Open;
        row.OpenedAt = now;
    }

    private Task<int> InsertAsync(PersonId person, Guid id, LifecycleChange.Insert insert, Instant now, CancellationToken ct)
    {
        var signal = insert.Signal;
        var level = LevelText(insert.Kind.Level);
        var items = signal.ItemIds.Select(i => i.Value).ToArray();
        var state = insert.Open ? NotificationStates.Open : NotificationStates.Pending;
        var openedAt = insert.Open ? now : (Instant?)null;
        var json = JsonSerializer.Serialize(signal.Params, StoreJson.Default.IReadOnlyDictionaryStringString);
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO platform.notification
                (id, person_id, kind, key, level, item_ids, due_on, notify_on, params, state, created_at, opened_at)
            VALUES
                ({id}, {person.Value}, {insert.Kind.Kind}, {signal.Key}, {level}, {items}, {signal.DueOn}, {insert.NotifyOn},
                 CAST({json} AS jsonb), {state}, {now}, {openedAt})
            ON CONFLICT (person_id, key) DO NOTHING
            """, ct);
    }

    private sealed record PublishedRow(Guid Id, int Round);

    public async Task<OpenedNotification?> PublishAsync(PersonId person, NotificationKind kind, Notice notice, Instant now, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        var level = LevelText(kind.Level);
        var items = notice.ItemIds.Select(i => i.Value).ToArray();
        var json = JsonSerializer.Serialize(notice.Params, StoreJson.Default.IReadOnlyDictionaryStringString);
        var today = now.InUtc().Date;
        // One statement: a new key inserts, a resolved one reopens in its next round, any other state is left as it is (no row returned).
        var rows = await db.Database.SqlQuery<PublishedRow>($"""
            INSERT INTO platform.notification
                (id, person_id, kind, key, level, item_ids, notify_on, params, state, created_at, opened_at)
            VALUES
                ({id}, {person.Value}, {kind.Kind}, {notice.Key}, {level}, {items}, {today}, CAST({json} AS jsonb), 'open', {now}, {now})
            ON CONFLICT (person_id, key) DO UPDATE SET
                state = 'open', round = notification.round + 1, opened_at = EXCLUDED.opened_at, notify_on = EXCLUDED.notify_on,
                params = EXCLUDED.params, item_ids = EXCLUDED.item_ids, read_at = NULL, resolved_at = NULL, dismissed_at = NULL,
                snoozed_until = NULL, digested_at = NULL, digest_id = NULL
            WHERE notification.state = 'resolved'
            RETURNING id AS "Id", round AS "Round"
            """).ToListAsync(ct);
        return rows.Count == 0 ? null : new OpenedNotification(rows[0].Id, kind.Level, rows[0].Round, notice.Key, notice.ItemIds, kind.Kind, now);
    }

    public async Task ResolveKeyAsync(string key, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        // A dismissal lasts one incident: it is settled too.
        await db.Notifications.Where(n => n.Key == key && n.State != NotificationStates.Resolved)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.State, NotificationStates.Resolved).SetProperty(n => n.ResolvedAt, now), ct);
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

    public async Task<NotificationRecord?> FindOpenByKeyAsync(string key, CancellationToken ct)
    {
        var row = await db.Notifications.AsNoTracking().Where(n => n.Key == key && n.State == NotificationStates.Open)
            .OrderBy(n => n.OpenedAt).ThenBy(n => n.Id).FirstOrDefaultAsync(ct);
        return row is null ? null : ToRecord(row);
    }

    public async Task MarkReadAsync(PersonId person, IReadOnlyCollection<Guid> ids, Instant at, CancellationToken ct)
    {
        var wanted = ids.ToArray();
        await db.Notifications.Where(n => n.PersonId == person.Value && wanted.Contains(n.Id) && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, at)
                .SetProperty(n => n.State, n => EndsWhenRead.Contains(n.Kind) ? NotificationStates.Resolved : n.State)
                .SetProperty(n => n.ResolvedAt, n => EndsWhenRead.Contains(n.Kind) ? at : n.ResolvedAt), ct);
    }

    public async Task SnoozeAsync(Guid id, LocalDate until, CancellationToken ct)
        => await db.Notifications.Where(n => n.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.State, NotificationStates.Snoozed).SetProperty(n => n.SnoozedUntil, until), ct);

    public async Task DismissAsync(Guid id, Instant at, CancellationToken ct)
        => await db.Notifications.Where(n => n.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.State, NotificationStates.Dismissed).SetProperty(n => n.DismissedAt, at), ct);

    public async Task<NotificationPreferences> PreferencesAsync(PersonId person, CancellationToken ct)
    {
        var row = await db.PreferenceRows.AsNoTracking().FirstOrDefaultAsync(r => r.PersonId == person.Value, ct);
        return row is null ? NotificationPreferences.Defaults : ToPreferences(row);
    }

    public async Task<NotificationPreferences?> SavePreferencesAsync(PersonId person, NotificationPreferences next, CancellationToken ct)
    {
        var row = await db.PreferenceRows.FirstOrDefaultAsync(r => r.PersonId == person.Value, ct);
        if ((row?.Version ?? 0) != next.Version)
            return null;

        if (row is null)
            db.PreferenceRows.Add(row = new NotificationPreferencesRow { PersonId = person.Value });
        row.EmailEnabled = next.EmailEnabled;
        row.ActionMode = next.Action.ToString().ToLowerInvariant();
        row.InfoMode = next.Info.ToString().ToLowerInvariant();
        row.Frequency = next.Frequency.ToString().ToLowerInvariant();
        row.Weekday = (int)next.Weekday;
        row.LeadDays = JsonSerializer.Serialize(next.LeadDays, StoreJson.Default.IReadOnlyDictionaryStringInt32);
        row.Version = next.Version + 1;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent save: the version moved (update) or the first row exists (insert). The stale entry must not be saved again.
            db.Entry(row).State = EntityState.Detached;
            return null;
        }

        return ToPreferences(row);
    }

    public async Task<int> PurgeAsync(Instant olderThan, CancellationToken ct)
    {
        var purged = db.Notifications.Where(n => (n.State == NotificationStates.Resolved || n.State == NotificationStates.Dismissed) && n.ResolvedAt < olderThan);
        // The deliveries go with their notification, except the household's: it outlives the row (FK set null), so its key stays spent.
        await db.Deliveries.Where(d => d.HouseholdKey == null && purged.Any(n => n.Id == d.NotificationId)).ExecuteDeleteAsync(ct);
        return await purged.ExecuteDeleteAsync(ct);
    }

    /// <summary>A row shows only when every item it concerns is visible: <c>item_ids &lt;@ visible</c>.</summary>
    private static IQueryable<NotificationRow> Visible(IQueryable<NotificationRow> query, IReadOnlySet<ItemId> visible)
    {
        var allowed = visible.Select(i => i.Value).ToArray();
        return query.Where(n => n.ItemIds.All(i => allowed.Contains(i)));
    }

    private static NotificationPreferences ToPreferences(NotificationPreferencesRow row) => new(
        row.EmailEnabled, Enum.Parse<ActionMode>(row.ActionMode, ignoreCase: true), Enum.Parse<InfoMode>(row.InfoMode, ignoreCase: true),
        Enum.Parse<DigestFrequency>(row.Frequency, ignoreCase: true), (IsoDayOfWeek)row.Weekday,
        JsonSerializer.Deserialize(row.LeadDays, StoreJson.Default.DictionaryStringInt32) ?? [], row.Version);

    private static string LevelText(NotificationLevel level) => level.ToString().ToLowerInvariant();

    internal static NotificationRecord ToRecord(NotificationRow row) => new(
        row.Id, new PersonId(row.PersonId), row.Kind, row.Key, Enum.Parse<NotificationLevel>(row.Level, ignoreCase: true),
        row.ItemIds.Select(i => new ItemId(i)).ToList(), row.DueOn, row.NotifyOn,
        JsonSerializer.Deserialize(row.Params, StoreJson.Default.DictionaryStringString) ?? [],
        row.State, row.ReadAt, row.SnoozedUntil, row.CreatedAt, row.OpenedAt, row.ResolvedAt, row.Round);
}
