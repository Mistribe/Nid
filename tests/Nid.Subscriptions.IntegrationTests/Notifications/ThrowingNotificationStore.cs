using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Platform.Notifications;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests.Notifications;

/// <summary>The real store, except that <see cref="ApplyAsync"/> throws for one person: a store failure outside the source.</summary>
public sealed class ThrowingNotificationStore(INotificationStore inner, PersonId failFor) : INotificationStore
{
    public Task<IReadOnlyList<StoredNotification>> ForSourceAsync(PersonId person, IReadOnlyCollection<string> kinds, CancellationToken ct)
        => inner.ForSourceAsync(person, kinds, ct);

    public Task<IReadOnlyList<OpenedNotification>> ApplyAsync(PersonId person, IReadOnlyList<LifecycleChange> changes, CancellationToken ct)
        => person == failFor ? throw new InvalidOperationException("The store is down for this person.") : inner.ApplyAsync(person, changes, ct);

    public Task<OpenedNotification?> PublishAsync(PersonId person, NotificationKind kind, Notice notice, Instant now, CancellationToken ct)
        => inner.PublishAsync(person, kind, notice, now, ct);

    public Task ResolveKeyAsync(string key, CancellationToken ct) => inner.ResolveKeyAsync(key, ct);

    public Task<LocalDate?> LastEvaluatedAsync(PersonId person, CancellationToken ct) => inner.LastEvaluatedAsync(person, ct);

    public Task RecordRunAsync(PersonId person, string source, LocalDate on, bool ok, CancellationToken ct)
        => inner.RecordRunAsync(person, source, on, ok, ct);

    public Task<Instant?> LastRunAtAsync(PersonId person, CancellationToken ct) => inner.LastRunAtAsync(person, ct);

    public Task<IReadOnlyList<NotificationRecord>> ListAsync(
        PersonId person, NotificationList list, IReadOnlySet<ItemId> visible, LocalDate today, Guid? before, int limit, CancellationToken ct)
        => inner.ListAsync(person, list, visible, today, before, limit, ct);

    public Task<(int Unread, int CriticalUnread)> CountAsync(PersonId person, IReadOnlySet<ItemId> visible, CancellationToken ct)
        => inner.CountAsync(person, visible, ct);

    public Task<NotificationRecord?> FindAsync(PersonId person, Guid id, CancellationToken ct) => inner.FindAsync(person, id, ct);

    public Task<NotificationRecord?> FindOpenByKeyAsync(string key, CancellationToken ct) => inner.FindOpenByKeyAsync(key, ct);

    public Task MarkReadAsync(PersonId person, IReadOnlyCollection<Guid> ids, Instant at, CancellationToken ct)
        => inner.MarkReadAsync(person, ids, at, ct);

    public Task SnoozeAsync(Guid id, LocalDate until, CancellationToken ct) => inner.SnoozeAsync(id, until, ct);

    public Task DismissAsync(Guid id, Instant at, CancellationToken ct) => inner.DismissAsync(id, at, ct);

    public Task<int> PurgeAsync(Instant olderThan, IReadOnlyCollection<string> declaredKinds, CancellationToken ct) => inner.PurgeAsync(olderThan, declaredKinds, ct);

    public Task<NotificationPreferences> PreferencesAsync(PersonId person, CancellationToken ct) => inner.PreferencesAsync(person, ct);

    public Task<NotificationPreferences?> SavePreferencesAsync(PersonId person, NotificationPreferences next, CancellationToken ct)
        => inner.SavePreferencesAsync(person, next, ct);
}
