using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;

namespace Nid.Platform.Notifications;

/// <summary>A row that just opened: what the outbox needs to decide whether to send it (notifications design §5.1).</summary>
public sealed record OpenedNotification(Guid Id, NotificationLevel Level, int Round, string Key, IReadOnlyList<ItemId> ItemIds);

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

/// <summary>The household's message, queued once per <paramref name="Key"/> whoever opens it first: channel <c>discord_household</c>, round 1.</summary>
public sealed record NewHouseholdDelivery(Guid NotificationId, PersonId Person, string Channel, string Key);

/// <summary>A digest message to queue: one per (digest, channel).</summary>
public sealed record NewDigestDelivery(Guid DigestId, PersonId Person, string Channel);

/// <summary>A message that ended failed: where, what kind of notification, when it was queued and the error code. No content, no person.</summary>
public sealed record FailedDelivery(Guid Id, string Channel, string Kind, Instant At, string Error);

/// <summary>A queued message as the dispatcher sees it: one notification, or a digest (<paramref name="DigestId"/>). <paramref name="Attempts"/>: the sends already tried.</summary>
public sealed record DeliveryRecord(Guid Id, Guid? NotificationId, PersonId Person, string Channel, int Round, int Attempts, Guid? DigestId = null, string? HouseholdKey = null);

/// <summary>Persistence of the outbox.</summary>
public interface IDeliveryStore
{
    /// <summary>Queues the messages, due at <paramref name="now"/>; one that already exists for (notification, channel, round) is ignored.</summary>
    Task EnqueueAsync(IReadOnlyList<NewDelivery> deliveries, Instant now, CancellationToken ct);

    /// <summary>Queues the household's messages, due at <paramref name="now"/>; one whose key already has a message, sent or not, is ignored.</summary>
    Task EnqueueHouseholdAsync(IReadOnlyList<NewHouseholdDelivery> deliveries, Instant now, CancellationToken ct);

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
    public const string DiscordPerson = "discord_person";
    public const string DiscordHousehold = "discord_household";

    /// <summary>
    /// The address on the channel, null when the channel does not reach the person: it is not available, the person did not turn
    /// it on, or they have no address. For Discord the address is the webhook, when it is <c>ok</c>; the household channel
    /// is queued by its own rule, never through here.
    /// </summary>
    public static string? AddressFor(INotificationChannel channel, PersonInfo? person, NotificationPreferences prefs, StoredWebhook? webhook)
        => !channel.IsAvailable ? null
            : channel.Name switch
            {
                Email => prefs.EmailEnabled && !string.IsNullOrWhiteSpace(person?.Email) ? person.Email : null,
                DiscordPerson => WebhookAddress(webhook),
                _ => null,
            };

    /// <summary>The webhook URL when it can be used: a broken one, or one that could not be read (empty URL), reaches nobody.</summary>
    public static string? WebhookAddress(StoredWebhook? webhook)
        => webhook is { Status: WebhookStates.Ok, Url.Length: > 0 } ? webhook.Url : null;
}

public sealed class DeliveryOutbox(
    IDeliveryStore store, IEnumerable<INotificationChannel> channels, IPersonRegistry persons, IWebhookStore webhooks, IVisibleItems visible, IClock clock) : IDeliveryOutbox
{
    public async Task EnqueueAsync(PersonId person, IReadOnlyList<OpenedNotification> opened, NotificationPreferences prefs, CancellationToken ct)
    {
        await EnqueueHouseholdAsync(person, opened, ct);
        var immediate = opened.Where(o => OutboxRules.IsImmediate(o.Level, prefs)).ToList();
        if (immediate.Count == 0)
            return;

        var info = await persons.FindAsync(person, ct);
        var webhook = await webhooks.FindAsync(person, ct);
        var reaching = channels.Where(c => ChannelAccess.AddressFor(c, info, prefs, webhook) is not null).ToList();
        var deliveries = (from row in immediate from channel in reaching select new NewDelivery(row.Id, person, channel.Name, row.Round)).ToList();
        if (deliveries.Count > 0)
            await store.EnqueueAsync(deliveries, clock.GetCurrentInstant(), ct);
    }

    /// <summary>
    /// The household channel: the first opening of a critical or action row about items every active adult can read, when its webhook
    /// works. The store keeps one message per key, so the second adult to open the same key queues nothing.
    /// </summary>
    private async Task EnqueueHouseholdAsync(PersonId person, IReadOnlyList<OpenedNotification> opened, CancellationToken ct)
    {
        var candidates = opened.Where(o => o is { Round: 1, ItemIds.Count: > 0, Level: NotificationLevel.Critical or NotificationLevel.Action }).ToList();
        if (candidates.Count == 0 || ChannelAccess.WebhookAddress(await webhooks.FindAsync(null, ct)) is null
            || !channels.Any(c => c.Name == ChannelAccess.DiscordHousehold && c.IsAvailable))
            return;

        var deliveries = new List<NewHouseholdDelivery>();
        foreach (var row in candidates)
            if (await HouseholdAccess.EveryAdultReadsAsync(persons, visible, row.ItemIds, ct))
                deliveries.Add(new NewHouseholdDelivery(row.Id, person, ChannelAccess.DiscordHousehold, row.Key));
        if (deliveries.Count > 0)
            await store.EnqueueHouseholdAsync(deliveries, clock.GetCurrentInstant(), ct);
    }
}

internal static class HouseholdAccess
{
    /// <summary>True when every active adult (not pending) can read every item: what the household may be told about.</summary>
    public static async Task<bool> EveryAdultReadsAsync(IPersonRegistry persons, IVisibleItems visible, IReadOnlyList<ItemId> items, CancellationToken ct)
    {
        foreach (var adult in (await persons.ListAsync(ct)).Where(p => p is { Kind: PersonKind.Adult, IsPending: false }))
        {
            var readable = await visible.ForAsync(adult.Id, VisibilityScope.Me, ct);
            if (!items.All(readable.Contains))
                return false;
        }

        return true;
    }
}
