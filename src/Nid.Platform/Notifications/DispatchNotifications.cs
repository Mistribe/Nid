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

    RenderedMessage Digest(IReadOnlyList<(NotificationRecord Row, NotificationKind Kind)> rows, IReadOnlyDictionary<ItemId, string> names);
}

[RequestHandler<DispatchNotificationsCommand, DispatchSummary>]
public sealed partial class DispatchNotificationsHandler(
    IDeliveryStore deliveries, IDigestStore digests, INotificationStore store, NotificationCatalog catalog, IEnumerable<INotificationChannel> channels,
    IPersonRegistry persons, IItemRegistry items, IVisibleItems visible, INotificationRenderer renderer, IClock clock,
    ILogger<DispatchNotificationsHandler> logger) : IRequestHandler<DispatchNotificationsCommand, DispatchSummary>
{
    public const int BatchSize = 50;

    private const string RenderFailed = "notifications.render_failed";
    private const string DispatchCrashed = "notifications.dispatch_crashed";

    private enum Outcome { None, Sent, Skipped, Failed, Retried }

    /// <summary>The failure code, and the text kept as <c>last_error</c>: the code and the status the channel gave, never content.</summary>
    private sealed record SendFailure(string Code, string Error)
    {
        public static SendFailure Of(UnambitiousFx.Functional.Failures.Failure failure)
            => new(failure.Code, NotificationChannelCodes.StatusOf(failure) is { } status ? $"{failure.Code} {status}" : failure.Code);
    }

    public async ValueTask<Result<DispatchSummary>> HandleAsync(DispatchNotificationsCommand command, CancellationToken ct = default)
    {
        var counts = new Dictionary<Outcome, int>();
        foreach (var due in await deliveries.DueAsync(clock.GetCurrentInstant(), BatchSize, ct))
        {
            Outcome outcome;
            try
            {
                outcome = await DispatchOneAsync(due, ct);
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
            await deliveries.SettleAsync(
                due.Id, next is null ? DeliveryStates.Failed : DeliveryStates.Pending, due.Attempts + 1, next, DispatchCrashed, null, ct);
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

    private async Task<Outcome> DispatchOneAsync(DeliveryRecord due, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        // A re-read, not a lock: safe with one API instance, since the job is [DisallowConcurrentExecution].
        if (await deliveries.ClaimAsync(due.Id, now, ct) is not { } delivery)
            return Outcome.None;

        var readable = await visible.ForAsync(delivery.Person, VisibilityScope.Me, ct);
        var rows = new List<(NotificationRecord Row, NotificationKind Kind)>();
        if (delivery is { DigestId: { } digestId })
        {
            // A digest says what is still open and readable; if nothing is, there is nothing to say.
            foreach (var open in await digests.OpenRowsAsync(digestId, ct))
                if (catalog.Find(open.Kind) is { } openKind && open.ItemIds.All(readable.Contains))
                    rows.Add((open, openKind));
        }
        else if (delivery.NotificationId is { } notificationId)
        {
            var found = await store.FindAsync(delivery.Person, notificationId, ct);
            var foundKind = found is null ? null : catalog.Find(found.Kind);
            if (found is { State: NotificationStates.Open } && found.Round == delivery.Round && foundKind is not null && found.ItemIds.All(readable.Contains))
                rows.Add((found, foundKind));
        }

        if (rows.Count == 0)
            return await SkipAsync(delivery, ct);

        var channel = channels.FirstOrDefault(c => c.Name == delivery.Channel);
        var address = channel is null
            ? null
            : ChannelAccess.AddressFor(channel, await persons.FindAsync(delivery.Person, ct), await store.PreferencesAsync(delivery.Person, ct));
        if (channel is null || address is null)
            return await SkipAsync(delivery, ct);

        RenderedMessage message;
        try
        {
            var found = await items.FindManyAsync(rows.SelectMany(r => r.Row.ItemIds).Distinct().ToList(), ct);
            var names = found.ToDictionary(f => f.Key, f => f.Value.DisplayName);
            message = delivery.DigestId is null ? renderer.One(rows[0].Row, rows[0].Kind, names) : renderer.Digest(rows, names);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            LogRenderFailed(e, delivery.Id);
            await deliveries.SettleAsync(delivery.Id, DeliveryStates.Failed, delivery.Attempts + 1, null, RenderFailed, null, ct);
            return Outcome.Failed;
        }

        var failed = await SendAsync(channel, message, new ChannelTarget(delivery.Person, address), ct);
        var attempts = delivery.Attempts + 1;
        if (failed is null)
        {
            await deliveries.SettleAsync(delivery.Id, DeliveryStates.Sent, attempts, null, null, clock.GetCurrentInstant(), ct);
            return Outcome.Sent;
        }

        var next = failed.Code == NotificationChannelCodes.Rejected ? null : OutboxRules.NextAttempt(delivery.Attempts, clock.GetCurrentInstant());
        if (next is null)
        {
            await deliveries.SettleAsync(delivery.Id, DeliveryStates.Failed, attempts, null, failed.Error, null, ct);
            return Outcome.Failed;
        }

        await deliveries.SettleAsync(delivery.Id, DeliveryStates.Pending, attempts, next, failed.Error, null, ct);
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
            return new SendFailure(NotificationChannelCodes.Unreachable, NotificationChannelCodes.Unreachable);
        }
    }

    private async Task<Outcome> SkipAsync(DeliveryRecord delivery, CancellationToken ct)
    {
        await deliveries.SettleAsync(delivery.Id, DeliveryStates.Skipped, delivery.Attempts, null, null, null, ct);
        return Outcome.Skipped;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivery {Delivery} could not be dispatched")]
    private partial void LogCrashed(Exception exception, Guid delivery);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivery {Delivery} could not be worded")]
    private partial void LogRenderFailed(Exception exception, Guid delivery);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The {Channel} channel threw while sending")]
    private partial void LogSendCrashed(Exception exception, string channel);
}
