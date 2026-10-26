using Nid.Platform.Contracts;
using NodaTime;

namespace Nid.Platform.Notifications;

/// <summary>A stored notification as the lifecycle sees it. <paramref name="Settled"/>: its <c>resolved_at</c> is set.</summary>
public sealed record StoredNotification(Guid Id, string Key, string State, LocalDate NotifyOn, LocalDate? SnoozedUntil, bool Settled = false);

/// <summary>One state change <see cref="NotificationLifecycle.Plan"/> asks the store to make (notifications design §3.3).</summary>
public abstract record LifecycleChange
{
    public sealed record Insert(Signal Signal, NotificationKind Kind, LocalDate NotifyOn, bool Open) : LifecycleChange;

    /// <summary>A pending row whose date moved; it opens when the new date has come.</summary>
    public sealed record Retime(Guid Id, LocalDate NotifyOn, bool Open, Signal Signal) : LifecycleChange;

    public sealed record OpenNow(Guid Id) : LifecycleChange;

    public sealed record Resolve(Guid Id) : LifecycleChange;

    /// <summary>A resolved row whose key is signalled again: back to open (or pending), unread, with the signal's figures.</summary>
    public sealed record Reopen(Guid Id, LocalDate NotifyOn, bool Open, Signal Signal) : LifecycleChange;

    /// <summary>A dismissed row whose key is no longer signalled: stamps <c>resolved_at</c>, the state stays dismissed.</summary>
    public sealed record SettleDismissed(Guid Id) : LifecycleChange;

    /// <summary>A settled dismissed row whose key is signalled again: clears <c>resolved_at</c>, so it is not purged while signalled.</summary>
    public sealed record Unsettle(Guid Id) : LifecycleChange;

    /// <summary>A snoozed row whose snooze is over on <paramref name="Today"/>, back to open.</summary>
    public sealed record Wake(Guid Id, LocalDate Today) : LifecycleChange;
}

/// <summary>The pure part of a source's evaluation: what to change given what is stored and what is signalled (notifications design §3.3).</summary>
public static class NotificationLifecycle
{
    /// <summary>
    /// <paramref name="existing"/> is one source's rows for one person. A dismissed key stays dismissed while it is signalled;
    /// a resolved key signalled again reopens. A stored row whose key is no longer signalled resolves; a dismissed one is
    /// settled instead (its end date is stamped, it is never purged while still signalled).
    /// </summary>
    public static IReadOnlyList<LifecycleChange> Plan(
        IReadOnlyList<StoredNotification> existing,
        IReadOnlyList<Signal> signals,
        IReadOnlyDictionary<string, NotificationKind> kinds,
        LocalDate today)
    {
        var changes = new List<LifecycleChange>();
        var stored = new Dictionary<string, StoredNotification>();
        foreach (var row in existing)
            stored.TryAdd(row.Key, row);

        var seen = new HashSet<string>();
        foreach (var signal in signals)
        {
            if (!kinds.TryGetValue(signal.Kind, out var kind) || !seen.Add(signal.Key))
                continue;

            var notifyOn = NotifyOn(signal, kind, today);
            if (!stored.TryGetValue(signal.Key, out var row))
            {
                changes.Add(new LifecycleChange.Insert(signal, kind, notifyOn, notifyOn <= today));
                continue;
            }

            switch (row.State)
            {
                case NotificationStates.Pending when notifyOn != row.NotifyOn:
                    changes.Add(new LifecycleChange.Retime(row.Id, notifyOn, notifyOn <= today, signal));
                    break;
                case NotificationStates.Pending when notifyOn <= today:
                    changes.Add(new LifecycleChange.OpenNow(row.Id));
                    break;
                case NotificationStates.Snoozed when row.SnoozedUntil is { } until && until <= today:
                    changes.Add(new LifecycleChange.Wake(row.Id, today));
                    break;
                case NotificationStates.Dismissed when row.Settled:
                    changes.Add(new LifecycleChange.Unsettle(row.Id));
                    break;
                case NotificationStates.Resolved:
                    changes.Add(new LifecycleChange.Reopen(row.Id, notifyOn, notifyOn <= today, signal));
                    break;
            }
        }

        foreach (var row in existing)
        {
            if (seen.Contains(row.Key))
                continue;
            if (row.State is NotificationStates.Open or NotificationStates.Pending or NotificationStates.Snoozed)
                changes.Add(new LifecycleChange.Resolve(row.Id));
            else if (row.State == NotificationStates.Dismissed && !row.Settled)
                changes.Add(new LifecycleChange.SettleDismissed(row.Id));
        }

        return changes;
    }

    /// <summary>
    /// The day a signal starts to show: its due date minus the kind's lead, never after the due date; without both, today.
    /// It may be in the past, then the row opens today.
    /// </summary>
    public static LocalDate NotifyOn(Signal signal, NotificationKind kind, LocalDate today)
    {
        if (signal.DueOn is not { } due || kind.LeadDays is not { } lead)
            return today;
        var on = due.PlusDays(-lead);
        return on > due ? due : on;
    }
}
