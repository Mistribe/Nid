using Microsoft.Extensions.Logging;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Notifications;

/// <summary>Sends the queued messages that are due (notifications design §5.1). Run every minute by the host as the system actor.</summary>
public sealed record DispatchNotificationsCommand : ICommand<DispatchSummary>, IAdminOnly;

/// <summary>What a run did: messages sent, skipped (no longer sendable), failed for good, and put back for a retry.</summary>
public sealed record DispatchSummary(int Sent, int Skipped, int Failed, int Retried);

/// <summary>Words a message for the outside: no amount, never the content of another person's data (notifications design §5.3).</summary>
public interface INotificationRenderer
{
    RenderedMessage One(NotificationRecord n, NotificationKind kind, IReadOnlyDictionary<ItemId, string> names);

    /// <summary>The household's message: the title, the due date, the link; no item name.</summary>
    RenderedMessage Household(NotificationRecord n, NotificationKind kind);

    RenderedMessage Digest(IReadOnlyList<(NotificationRecord Row, NotificationKind Kind)> rows, IReadOnlyDictionary<ItemId, string> names);
}

[RequestHandler<DispatchNotificationsCommand, DispatchSummary>]
public sealed partial class DispatchNotificationsHandler(
    IDeliveryStore deliveries, IDigestStore digests, INotificationStore store, NotificationCatalog catalog, IEnumerable<INotificationChannel> channels,
    IPersonRegistry persons, IWebhookStore webhooks, IItemRegistry items, IVisibleItems visible, INotificationRenderer renderer, IClock clock,
    ILogger<DispatchNotificationsHandler> logger) : IRequestHandler<DispatchNotificationsCommand, DispatchSummary>
{
    public const int BatchSize = 50;

    /// <summary>How far a message is put back when its target answered unreachable earlier in the run: the first retry's delay.</summary>
    public static readonly NodaTime.Duration DeferredBy = NodaTime.Duration.FromMinutes(1);

    private const string RenderFailed = "notifications.render_failed";
    private const string DispatchCrashed = "notifications.dispatch_crashed";

    private enum Outcome { None, Sent, Skipped, Failed, Retried }

    /// <summary>
    /// The failure code, and the text kept as <c>last_error</c>: the code and the status the channel gave, never content.
    /// <paramref name="Threw"/>: the channel crashed rather than answered.
    /// </summary>
    private sealed record SendFailure(string Code, string Error, bool Threw = false)
    {
        public static SendFailure Of(UnambitiousFx.Functional.Failures.Failure failure)
            => new(failure.Code, NotificationChannelCodes.StatusOf(failure) is { } status ? $"{failure.Code} {status}" : failure.Code);
    }

    public async ValueTask<Result<DispatchSummary>> HandleAsync(DispatchNotificationsCommand command, CancellationToken ct = default)
    {
        var counts = new Dictionary<Outcome, int>();
        var unreachable = new HashSet<string>(); // the targets that answered unreachable in this run
        foreach (var due in await deliveries.DueAsync(clock.GetCurrentInstant(), BatchSize, ct))
        {
            Outcome outcome;
            try
            {
                outcome = await DispatchOneAsync(due, unreachable, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                // Best effort: count the attempt and schedule the next one, so a delivery that always crashes ends failed
                // instead of being tried every minute; the others go on.
                LogCrashed(e, due.Id);
                await RecordCrashAsync(due, ct);
                continue;
            }

            counts[outcome] = counts.GetValueOrDefault(outcome) + 1;
        }

        return Result.Success(new DispatchSummary(
            counts.GetValueOrDefault(Outcome.Sent), counts.GetValueOrDefault(Outcome.Skipped), counts.GetValueOrDefault(Outcome.Failed),
            counts.GetValueOrDefault(Outcome.Retried)));
    }

    private async Task RecordCrashAsync(DeliveryRecord due, CancellationToken ct)
    {
        try
        {
            var now = clock.GetCurrentInstant();
            var next = OutboxRules.NextAttempt(due.Attempts, now);
            await SettleAsync(due, next is null ? DeliveryStates.Failed : DeliveryStates.Pending, due.Attempts + 1, next, DispatchCrashed, null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            LogCrashed(e, due.Id);
        }
    }

    private async Task<Outcome> DispatchOneAsync(DeliveryRecord due, HashSet<string> unreachable, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        // A re-read, not a lock: safe with one API instance, since the job is [DisallowConcurrentExecution].
        if (await deliveries.ClaimAsync(due.Id, now, ct) is not { } delivery)
            return Outcome.None;

        var household = delivery.Channel == ChannelAccess.DiscordHousehold;
        var rows = new List<(NotificationRecord Row, NotificationKind Kind)>();
        if (delivery is { DigestId: { } digestId })
        {
            var readable = await visible.ForAsync(delivery.Person, VisibilityScope.Me, ct);
            // A digest says what is still open and readable; if nothing is, there is nothing to say.
            foreach (var open in await digests.OpenRowsAsync(digestId, ct))
                if (catalog.Find(open.Kind) is { } openKind && open.ItemIds.All(readable.Contains))
                    rows.Add((open, openKind));
        }
        else if (household)
        {
            // Any adult's open row for the key will do: the first opener's may be gone, snoozed or purged.
            var found = delivery.HouseholdKey is { } key ? await store.FindOpenByKeyAsync(HouseholdAccess.RowKeyOf(key), ct) : null;
            var foundKind = found is null ? null : catalog.Find(found.Kind);
            if (found is not null && foundKind is not null && await HouseholdAccess.MayTellAsync(persons, visible, found, foundKind, ct))
                rows.Add((found, foundKind));
        }
        else if (delivery.NotificationId is { } notificationId)
        {
            var readable = await visible.ForAsync(delivery.Person, VisibilityScope.Me, ct);
            var found = await store.FindAsync(delivery.Person, notificationId, ct);
            var foundKind = found is null ? null : catalog.Find(found.Kind);
            if (found is { State: NotificationStates.Open } && found.Round == delivery.Round && foundKind is not null && found.ItemIds.All(readable.Contains))
                rows.Add((found, foundKind));
        }

        if (rows.Count == 0)
            return await SkipAsync(delivery, ct);

        var channel = channels.FirstOrDefault(c => c.Name == delivery.Channel);
        var address = channel is null ? null
            : household ? (channel.IsAvailable ? ChannelAccess.WebhookAddress(await webhooks.FindAsync(null, ct)) : null)
            : ChannelAccess.AddressFor(
                channel, await persons.FindAsync(delivery.Person, ct), await store.PreferencesAsync(delivery.Person, ct),
                await webhooks.FindAsync(delivery.Person, ct));
        if (channel is null || address is null)
            return await SkipAsync(delivery, ct);
        // The target the send reaches: the email channel's SMTP server is one for every address, a webhook is its own.
        var target = channel.Name == ChannelAccess.Email ? channel.Name : $"{channel.Name} {address}";
        if (unreachable.Contains(target))
        {
            // Deferred, not an attempt: only the next try moves, so a long outage does not keep these at the head of the batch
            // ahead of the other channels' messages.
            await deliveries.DeferAsync(delivery.Id, clock.GetCurrentInstant() + DeferredBy, ct);
            return Outcome.None;
        }

        RenderedMessage message;
        try
        {
            if (household)
                // The household's link is the same for everyone: the first opener's admin flag is not theirs.
                message = renderer.Household(rows[0].Row with { Params = rows[0].Row.Params.Where(p => p.Key != "admin").ToDictionary() }, rows[0].Kind);
            else
            {
                var found = await items.FindManyAsync(rows.SelectMany(r => r.Row.ItemIds).Distinct().ToList(), ct);
                var names = found.ToDictionary(f => f.Key, f => f.Value.DisplayName);
                message = delivery.DigestId is null ? renderer.One(rows[0].Row, rows[0].Kind, names) : renderer.Digest(rows, names);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            LogRenderFailed(e, delivery.Id);
            await SettleAsync(delivery, DeliveryStates.Failed, delivery.Attempts + 1, null, RenderFailed, null, ct);
            return Outcome.Failed;
        }

        var failed = await SendAsync(channel, message, new ChannelTarget(delivery.Person, address), ct);
        var attempts = delivery.Attempts + 1;
        if (failed is null)
        {
            await SettleAsync(delivery, DeliveryStates.Sent, attempts, null, null, clock.GetCurrentInstant(), ct);
            return Outcome.Sent;
        }

        if (failed is { Code: NotificationChannelCodes.Unreachable, Threw: false })
            unreachable.Add(target);
        if (failed.Code == NotificationChannelCodes.Broken)
            await webhooks.MarkBrokenAsync(household ? null : delivery.Person, address, ct); // the next pending messages for it are skipped

        var next = failed.Code is NotificationChannelCodes.Rejected or NotificationChannelCodes.Broken ? null : OutboxRules.NextAttempt(delivery.Attempts, clock.GetCurrentInstant());
        if (next is null)
        {
            await SettleAsync(delivery, DeliveryStates.Failed, attempts, null, failed.Error, null, ct);
            return Outcome.Failed;
        }

        await SettleAsync(delivery, DeliveryStates.Pending, attempts, next, failed.Error, null, ct);
        return Outcome.Retried;
    }

    /// <summary>Null when the message went out, else the failure code and what is kept of it. A channel that throws is unreachable.</summary>
    private async Task<SendFailure?> SendAsync(INotificationChannel channel, RenderedMessage message, ChannelTarget target, CancellationToken ct)
    {
        try
        {
            var result = await channel.SendAsync(message, target, ct);
            return result.TryGetFailure(out var failure) ? SendFailure.Of(failure!) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            LogSendCrashed(e, channel.Name);
            return new SendFailure(NotificationChannelCodes.Unreachable, NotificationChannelCodes.Unreachable, Threw: true);
        }
    }

    private async Task<Outcome> SkipAsync(DeliveryRecord delivery, CancellationToken ct)
    {
        await SettleAsync(delivery, DeliveryStates.Skipped, delivery.Attempts, null, null, null, ct);
        return Outcome.Skipped;
    }

    private async Task SettleAsync(DeliveryRecord delivery, string state, int attempts, Instant? next, string? error, Instant? sentAt, CancellationToken ct)
    {
        await deliveries.SettleAsync(delivery.Id, state, attempts, next, error, sentAt, ct);
        // A digest that reached nobody on any channel gives its rows to the next one.
        if (delivery.DigestId is { } digestId && state is DeliveryStates.Failed or DeliveryStates.Skipped)
            await digests.ReleaseAsync(digestId, ct);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivery {Delivery} could not be dispatched")]
    private partial void LogCrashed(Exception exception, Guid delivery);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivery {Delivery} could not be worded")]
    private partial void LogRenderFailed(Exception exception, Guid delivery);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The {Channel} channel threw while sending")]
    private partial void LogSendCrashed(Exception exception, string channel);
}
