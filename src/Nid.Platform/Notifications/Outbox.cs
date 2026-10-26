using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;

namespace Nid.Platform.Notifications;

/// <summary>A row that just opened: what the outbox needs to decide whether to send it (notifications design §5.1).</summary>
public sealed record OpenedNotification(Guid Id, NotificationLevel Level, int Round);

/// <summary>The states of a delivery.</summary>
public static class DeliveryStates
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
}

/// <summary>Which opened rows go out at once and when a failed send is tried again (notifications design §5.1).</summary>
public static class OutboxRules
{
    private static readonly NodaTime.Duration[] Retries =
    [
        NodaTime.Duration.FromMinutes(1), NodaTime.Duration.FromMinutes(5), NodaTime.Duration.FromMinutes(30),
        NodaTime.Duration.FromHours(2), NodaTime.Duration.FromHours(12),
    ];

    /// <summary>Critical always; an action row when the person wants them at once; never an info row.</summary>
    public static bool IsImmediate(NotificationLevel level, NotificationPreferences prefs) => level switch
    {
        NotificationLevel.Critical => true,
        NotificationLevel.Action => prefs.Action == ActionMode.Immediate,
        _ => false,
    };

    /// <summary>
    /// Whether a row may be carried by a digest, with the preferences of the time: any action row (the store leaves out the ones already
    /// sent at once, so that switching the mode neither sends a row twice nor loses it), and info rows unless the person chose the centre only.
    /// </summary>
    public static bool GoesToDigest(NotificationLevel level, NotificationPreferences prefs) => level switch
    {
        NotificationLevel.Action => true,
        NotificationLevel.Info => prefs.Info == InfoMode.Digest,
        _ => false,
    };

    /// <summary>
    /// When to try again after a failed send, <paramref name="attempts"/> being the sends tried before this one: 1 min, 5 min,
    /// 30 min, 2 h, 12 h, then never (the delivery fails).
    /// </summary>
    public static Instant? NextAttempt(int attempts, Instant now)
        => attempts >= 0 && attempts < Retries.Length ? now + Retries[attempts] : null;
}

/// <summary>Queues the messages of the rows that just opened (notifications design §5.1).</summary>
public interface IDeliveryOutbox
{
    Task EnqueueAsync(PersonId person, IReadOnlyList<OpenedNotification> opened, NotificationPreferences prefs, CancellationToken ct);
}

/// <summary>One message to queue.</summary>
public sealed record NewDelivery(Guid NotificationId, PersonId Person, string Channel, int Round);

/// <summary>A digest message to queue: one per (digest, channel).</summary>
public sealed record NewDigestDelivery(Guid DigestId, PersonId Person, string Channel);

/// <summary>A message that ended failed: where, what kind of notification, when it was queued and the error code. No content, no person.</summary>
public sealed record FailedDelivery(Guid Id, string Channel, string Kind, Instant At, string Error);

/// <summary>A queued message as the dispatcher sees it: one notification, or a digest (<paramref name="DigestId"/>). <paramref name="Attempts"/>: the sends already tried.</summary>
public sealed record DeliveryRecord(Guid Id, Guid? NotificationId, PersonId Person, string Channel, int Round, int Attempts, Guid? DigestId = null);

/// <summary>Persistence of the outbox.</summary>
public interface IDeliveryStore
{
    /// <summary>Queues the messages, due at <paramref name="now"/>; one that already exists for (notification, channel, round) is ignored.</summary>
    Task EnqueueAsync(IReadOnlyList<NewDelivery> deliveries, Instant now, CancellationToken ct);

    /// <summary>Up to <paramref name="limit"/> pending messages due at <paramref name="now"/>, the oldest due first.</summary>
    Task<IReadOnlyList<DeliveryRecord>> DueAsync(Instant now, int limit, CancellationToken ct);

    /// <summary>The message read again, null unless it is still pending and due. A re-read, not a lock: safe with one API instance (the job is <c>[DisallowConcurrentExecution]</c>).</summary>
    Task<DeliveryRecord?> ClaimAsync(Guid id, Instant now, CancellationToken ct);

    /// <summary>
    /// Records the result of the attempt in one save: the state, the sends tried, the next try (pending only) and the error code.
    /// Only a pending message is settled.
    /// </summary>
    Task SettleAsync(Guid id, string state, int attempts, Instant? nextAttemptAt, string? lastError, Instant? sentAt, CancellationToken ct);

    /// <summary>The messages that ended failed since <paramref name="since"/>, the latest first, at most <paramref name="limit"/>. For the admin status.</summary>
    Task<IReadOnlyList<FailedDelivery>> FailedSinceAsync(Instant since, int limit, CancellationToken ct);
}

/// <summary>Which channels reach a person, and where.</summary>
internal static class ChannelAccess
{
    public const string Email = "email";

    /// <summary>
    /// The address on the channel, null when the channel does not reach the person: it is not available, the person did not turn
    /// it on, or they have no address.
    /// </summary>
    public static string? AddressFor(INotificationChannel channel, PersonInfo? person, NotificationPreferences prefs)
        => channel is { IsAvailable: true, Name: Email } && prefs.EmailEnabled && !string.IsNullOrWhiteSpace(person?.Email) ? person.Email : null;
}

public sealed class DeliveryOutbox(IDeliveryStore store, IEnumerable<INotificationChannel> channels, IPersonRegistry persons, IClock clock) : IDeliveryOutbox
{
    public async Task EnqueueAsync(PersonId person, IReadOnlyList<OpenedNotification> opened, NotificationPreferences prefs, CancellationToken ct)
    {
        var immediate = opened.Where(o => OutboxRules.IsImmediate(o.Level, prefs)).ToList();
        if (immediate.Count == 0)
            return;

        var info = await persons.FindAsync(person, ct);
        var reaching = channels.Where(c => ChannelAccess.AddressFor(c, info, prefs) is not null).ToList();
        var deliveries = (from row in immediate from channel in reaching select new NewDelivery(row.Id, person, channel.Name, row.Round)).ToList();
        if (deliveries.Count > 0)
            await store.EnqueueAsync(deliveries, clock.GetCurrentInstant(), ct);
    }
}
