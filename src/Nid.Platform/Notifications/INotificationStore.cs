using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;

namespace Nid.Platform.Notifications;

/// <summary>A stored notification as the centre shows it (notifications design §2.3).</summary>
public sealed record NotificationRecord(
    Guid Id, PersonId Person, string Kind, string Key, NotificationLevel Level, IReadOnlyList<ItemId> ItemIds, LocalDate? DueOn,
    LocalDate NotifyOn, IReadOnlyDictionary<string, string> Params, string State, Instant? ReadAt, LocalDate? SnoozedUntil,
    Instant CreatedAt, Instant? OpenedAt, Instant? ResolvedAt, int Round = 1);

/// <summary>The three lists of the centre: what to do, what is put off, what is over (notifications design §4).</summary>
public enum NotificationList { Todo, Snoozed, History }

/// <summary>Persistence of notifications and of the evaluation runs (notifications design §2.3, §4).</summary>
public interface INotificationStore
{
    /// <summary>The person's rows of the given kinds, in every state: the lifecycle needs the final ones to keep their keys.</summary>
    Task<IReadOnlyList<StoredNotification>> ForSourceAsync(PersonId person, IReadOnlyCollection<string> kinds, CancellationToken ct);

    /// <summary>
    /// Applies the changes of <see cref="NotificationLifecycle.Plan"/>. An insert whose (person, key) exists is ignored. Inserts are applied
    /// before the other changes and not in the same transaction (safe: evaluation is idempotent). Returns the rows that opened, with the
    /// round they opened in: a row that opens again after it was open (a wake, a reopen) is in its next round.
    /// </summary>
    Task<IReadOnlyList<OpenedNotification>> ApplyAsync(PersonId person, IReadOnlyList<LifecycleChange> changes, CancellationToken ct);

    /// <summary>The oldest "last successful day" over the person's sources, null when one never succeeded or none ran.</summary>
    Task<LocalDate?> LastEvaluatedAsync(PersonId person, CancellationToken ct);

    Task RecordRunAsync(PersonId person, string source, LocalDate on, bool ok, CancellationToken ct);

    /// <summary>When the person's sources were last evaluated, for the 15-minute refresh.</summary>
    Task<Instant?> LastRunAtAsync(PersonId person, CancellationToken ct);

    /// <summary>
    /// A page of one list, newest opened first then by id. A row shows only when all its items are in <paramref name="visible"/>.
    /// <paramref name="before"/> is the last id of the previous page. A <paramref name="before"/> that is not the person's row gives an empty page.
    /// </summary>
    Task<IReadOnlyList<NotificationRecord>> ListAsync(
        PersonId person, NotificationList list, IReadOnlySet<ItemId> visible, LocalDate today, Guid? before, int limit, CancellationToken ct);

    /// <summary>Open and unread rows, and among them the critical ones, over the visible items.</summary>
    Task<(int Unread, int CriticalUnread)> CountAsync(PersonId person, IReadOnlySet<ItemId> visible, CancellationToken ct);

    Task<NotificationRecord?> FindAsync(PersonId person, Guid id, CancellationToken ct);

    /// <summary>An open row with this key, whoever it belongs to (the household channel speaks for every adult); null when none is open.</summary>
    Task<NotificationRecord?> FindOpenByKeyAsync(string key, CancellationToken ct);

    Task MarkReadAsync(PersonId person, IReadOnlyCollection<Guid> ids, Instant at, CancellationToken ct);

    Task SnoozeAsync(Guid id, LocalDate until, CancellationToken ct);

    Task DismissAsync(Guid id, Instant at, CancellationToken ct);

    /// <summary>The person's preferences, <see cref="NotificationPreferences.Defaults"/> (version 0) when nothing was saved.</summary>
    Task<NotificationPreferences> PreferencesAsync(PersonId person, CancellationToken ct);

    /// <summary>
    /// Saves <paramref name="next"/> when the stored version is <c>next.Version</c> (0 for none) and returns it with the version
    /// bumped; null when someone saved meanwhile.
    /// </summary>
    Task<NotificationPreferences?> SavePreferencesAsync(PersonId person, NotificationPreferences next, CancellationToken ct);

    /// <summary>Deletes the resolved and dismissed rows that ended before <paramref name="olderThan"/>.</summary>
    Task<int> PurgeAsync(Instant olderThan, CancellationToken ct);
}
