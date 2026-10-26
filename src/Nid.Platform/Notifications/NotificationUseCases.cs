using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Notifications;

/// <summary>One row of the centre. No amount: <see cref="Body"/> names the item and the date (notifications design §4).</summary>
public sealed record NotificationView(
    Guid Id, string Kind, string Level, string Title, string Body, string? Link, IReadOnlyList<Guid> ItemIds, LocalDate? DueOn,
    DateTimeOffset ArrivedAt, bool Read, string State, LocalDate? SnoozedUntil, bool Critical);

/// <summary>Carries item ids, so the output guard checks every one (spec §6).</summary>
public sealed record NotificationPage(IReadOnlyList<NotificationView> Entries, Guid? Next) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Entries.SelectMany(e => e.ItemIds).Distinct().Select(i => new ItemId(i));
}

public sealed record NotificationCount(int Unread, int CriticalUnread);

/// <summary>What a notification command answers: nothing, the endpoint replies 204.</summary>
public sealed record NotificationsUpdated;

public enum SnoozePreset { Week, Month }

/// <summary>Limit 1 to 100, 50 by default.</summary>
public sealed record ListNotificationsQuery(NotificationList List, Guid? Before, int? Limit) : IQuery<NotificationPage>;

/// <summary>Also asks for the reader to be refreshed (notifications design §3.3).</summary>
public sealed record CountNotificationsQuery : IQuery<NotificationCount>;

public sealed record ReadNotificationCommand(Guid Id) : ICommand<NotificationsUpdated>;

/// <summary>The open, unread rows the reader can see.</summary>
public sealed record ReadAllNotificationsCommand : ICommand<NotificationsUpdated>;

/// <summary>Exactly one of <paramref name="Until"/> and <paramref name="Preset"/>: a week or a month from the household today.</summary>
public sealed record SnoozeNotificationCommand(Guid Id, LocalDate? Until, SnoozePreset? Preset) : ICommand<NotificationsUpdated>;

public sealed record DismissNotificationCommand(Guid Id) : ICommand<NotificationsUpdated>;

internal static class NotificationAccess
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;

    public static Failure Unauthorized() => new UnauthorizedFailure("Notifications belong to a person.", null);

    public static Failure NotFound(Guid id) => new NotFoundFailure("notification", id.ToString());

    /// <summary>
    /// The person's own row, when every item it is about is readable by them now: a row about an item that turned private is
    /// not theirs to act on, and its existence is not disclosed.
    /// </summary>
    public static async Task<NotificationRecord?> FindActionableAsync(
        INotificationStore store, IVisibleItems visible, PersonId me, Guid id, CancellationToken ct)
    {
        var record = await store.FindAsync(me, id, ct);
        if (record is null)
            return null;
        var readable = await visible.ForAsync(me, VisibilityScope.Me, ct);
        return record.ItemIds.All(readable.Contains) ? record : null;
    }
}

[RequestHandler<ListNotificationsQuery, NotificationPage>]
public sealed class ListNotificationsHandler(
    INotificationStore store, NotificationCatalog catalog, IItemRegistry items, IVisibleItems visible, IHouseholdCalendar calendar, IActorAccessor accessor)
    : IRequestHandler<ListNotificationsQuery, NotificationPage>
{
    public async ValueTask<Result<NotificationPage>> HandleAsync(ListNotificationsQuery query, CancellationToken ct = default)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Failure<NotificationPage>(NotificationAccess.Unauthorized());

        var limit = Math.Clamp(query.Limit ?? NotificationAccess.DefaultLimit, 1, NotificationAccess.MaxLimit);
        var readable = await visible.ForAsync(me, VisibilityScope.Me, ct);
        var rows = await store.ListAsync(me, query.List, readable, await calendar.TodayAsync(ct), query.Before, limit, ct);

        var found = await items.FindManyAsync(rows.SelectMany(r => r.ItemIds).Distinct().ToList(), ct);
        var names = found.ToDictionary(f => f.Key, f => f.Value.DisplayName);
        var entries = new List<NotificationView>();
        foreach (var row in rows)
            if (catalog.Find(row.Kind) is { } kind) // a kind no module declares any more cannot be worded
                entries.Add(ToView(row, kind, names));

        return Result.Success(new NotificationPage(entries, rows.Count == limit ? rows[^1].Id : null));
    }

    private static NotificationView ToView(NotificationRecord row, NotificationKind kind, IReadOnlyDictionary<ItemId, string> names)
    {
        var text = NotificationTemplates.Render(row, kind, names);
        return new NotificationView(
            row.Id, row.Kind, row.Level.ToString().ToLowerInvariant(), text.Title, text.Body, text.Link, row.ItemIds.Select(i => i.Value).ToList(),
            row.DueOn, (row.OpenedAt ?? row.CreatedAt).ToDateTimeOffset(), row.ReadAt is not null, row.State, row.SnoozedUntil, row.Level == NotificationLevel.Critical);
    }
}

[RequestHandler<CountNotificationsQuery, NotificationCount>]
public sealed class CountNotificationsHandler(INotificationStore store, IVisibleItems visible, INotificationRefresh refresh, IActorAccessor accessor)
    : IRequestHandler<CountNotificationsQuery, NotificationCount>
{
    public async ValueTask<Result<NotificationCount>> HandleAsync(CountNotificationsQuery query, CancellationToken ct = default)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Failure<NotificationCount>(NotificationAccess.Unauthorized());

        refresh.Request(me);
        var (unread, critical) = await store.CountAsync(me, await visible.ForAsync(me, VisibilityScope.Me, ct), ct);
        return Result.Success(new NotificationCount(unread, critical));
    }
}

[RequestHandler<ReadNotificationCommand, NotificationsUpdated>]
public sealed class ReadNotificationHandler(INotificationStore store, IVisibleItems visible, IClock clock, IActorAccessor accessor)
    : IRequestHandler<ReadNotificationCommand, NotificationsUpdated>
{
    public async ValueTask<Result<NotificationsUpdated>> HandleAsync(ReadNotificationCommand command, CancellationToken ct = default)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Failure<NotificationsUpdated>(NotificationAccess.Unauthorized());
        if (await NotificationAccess.FindActionableAsync(store, visible, me, command.Id, ct) is not { State: NotificationStates.Open or NotificationStates.Snoozed })
            return Result.Failure<NotificationsUpdated>(NotificationAccess.NotFound(command.Id));

        await store.MarkReadAsync(me, [command.Id], clock.GetCurrentInstant(), ct);
        return Result.Success(new NotificationsUpdated());
    }
}

[RequestHandler<ReadAllNotificationsCommand, NotificationsUpdated>]
public sealed class ReadAllNotificationsHandler(INotificationStore store, IVisibleItems visible, IHouseholdCalendar calendar, IClock clock, IActorAccessor accessor)
    : IRequestHandler<ReadAllNotificationsCommand, NotificationsUpdated>
{
    public async ValueTask<Result<NotificationsUpdated>> HandleAsync(ReadAllNotificationsCommand command, CancellationToken ct = default)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Failure<NotificationsUpdated>(NotificationAccess.Unauthorized());

        var readable = await visible.ForAsync(me, VisibilityScope.Me, ct);
        var today = await calendar.TodayAsync(ct);
        var unread = new List<Guid>();
        Guid? before = null;
        while (true)
        {
            var page = await store.ListAsync(me, NotificationList.Todo, readable, today, before, NotificationAccess.MaxLimit, ct);
            unread.AddRange(page.Where(n => n.ReadAt is null).Select(n => n.Id));
            if (page.Count < NotificationAccess.MaxLimit)
                break;
            before = page[^1].Id;
        }

        if (unread.Count > 0)
            await store.MarkReadAsync(me, unread, clock.GetCurrentInstant(), ct);
        return Result.Success(new NotificationsUpdated());
    }
}

[RequestHandler<SnoozeNotificationCommand, NotificationsUpdated>]
public sealed class SnoozeNotificationHandler(INotificationStore store, IVisibleItems visible, IHouseholdCalendar calendar, IActorAccessor accessor)
    : IRequestHandler<SnoozeNotificationCommand, NotificationsUpdated>
{
    private const int MaxDays = 365;

    public async ValueTask<Result<NotificationsUpdated>> HandleAsync(SnoozeNotificationCommand command, CancellationToken ct = default)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Failure<NotificationsUpdated>(NotificationAccess.Unauthorized());

        // Only an open or a snoozed row is acted on from the centre; the others are not there to be acted on.
        if (await NotificationAccess.FindActionableAsync(store, visible, me, command.Id, ct) is not { State: NotificationStates.Open or NotificationStates.Snoozed } row)
            return Result.Failure<NotificationsUpdated>(NotificationAccess.NotFound(command.Id));
        if (row.Level == NotificationLevel.Critical)
            return Result.Failure<NotificationsUpdated>(new Failure(NotificationCodes.NotSnoozable, "A critical notification cannot be snoozed.", null));

        var today = await calendar.TodayAsync(ct);
        if (command.Until is null == command.Preset is null)
            return Invalid("Give a date or a preset, not both.");
        var until = command.Until ?? (command.Preset == SnoozePreset.Week ? today.PlusDays(7) : today.PlusMonths(1));
        if (until <= today || until > today.PlusDays(MaxDays))
            return Invalid($"The date is after today and at most {MaxDays} days away.");

        await store.SnoozeAsync(row.Id, until, ct);
        return Result.Success(new NotificationsUpdated());
    }

    private static Result<NotificationsUpdated> Invalid(string message)
        => Result.Failure<NotificationsUpdated>(FieldErrors.At(new Failure(NotificationCodes.InvalidSnooze, message, null), "until"));
}

[RequestHandler<DismissNotificationCommand, NotificationsUpdated>]
public sealed class DismissNotificationHandler(INotificationStore store, IVisibleItems visible, IClock clock, IActorAccessor accessor)
    : IRequestHandler<DismissNotificationCommand, NotificationsUpdated>
{
    public async ValueTask<Result<NotificationsUpdated>> HandleAsync(DismissNotificationCommand command, CancellationToken ct = default)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Failure<NotificationsUpdated>(NotificationAccess.Unauthorized());

        if (await NotificationAccess.FindActionableAsync(store, visible, me, command.Id, ct) is not { State: NotificationStates.Open or NotificationStates.Snoozed } row)
            return Result.Failure<NotificationsUpdated>(NotificationAccess.NotFound(command.Id));
        if (row.Level == NotificationLevel.Critical)
            return Result.Failure<NotificationsUpdated>(new Failure(NotificationCodes.NotDismissable, "A critical notification cannot be dismissed.", null));

        await store.DismissAsync(row.Id, clock.GetCurrentInstant(), ct);
        return Result.Success(new NotificationsUpdated());
    }
}
