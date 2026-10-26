using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Platform.Notifications;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Admin;

/// <summary>A message that ended <c>failed</c>: no content and no person, only where, what and why (notifications design §6.4).</summary>
public sealed record FailedDeliveryView(Guid Id, string Channel, string Kind, DateTimeOffset At, string Error);

/// <summary>Whether email can go out on this installation, and the last 30 days of messages that failed for good.</summary>
public sealed record AdminNotificationsView(bool SmtpConfigured, string? SmtpHost, IReadOnlyList<FailedDeliveryView> Failures, WebhookView? Discord);

public sealed record GetAdminNotificationsQuery : IQuery<AdminNotificationsView>, IAdminOnly;

/// <summary>Sends the test message to the admin, like <see cref="TestChannelCommand"/>.</summary>
public sealed record AdminTestChannelCommand(string Channel) : ICommand<TestChannelResult>, IAdminOnly;

[RequestHandler<GetAdminNotificationsQuery, AdminNotificationsView>]
public sealed class GetAdminNotificationsHandler(IEmailAvailability email, IDeliveryStore deliveries, IWebhookStore webhooks, IClock clock)
    : IRequestHandler<GetAdminNotificationsQuery, AdminNotificationsView>
{
    public const int Days = 30;
    public const int Limit = 100;

    public async ValueTask<Result<AdminNotificationsView>> HandleAsync(GetAdminNotificationsQuery query, CancellationToken ct = default)
    {
        var since = clock.GetCurrentInstant() - NodaTime.Duration.FromDays(Days);
        var failed = await deliveries.FailedSinceAsync(since, Limit, ct);
        return Result.Success(new AdminNotificationsView(
            email.IsConfigured, email.IsConfigured ? email.Host : null,
            failed.Select(f => new FailedDeliveryView(f.Id, f.Channel, f.Kind, f.At.ToDateTimeOffset(), f.Error)).ToList(),
            WebhookUrls.ViewOf(await webhooks.FindAsync(null, ct))));
    }
}

[RequestHandler<AdminTestChannelCommand, TestChannelResult>]
public sealed class AdminTestChannelHandler(
    IEnumerable<INotificationChannel> channels, IEmailAvailability email, IPersonRegistry persons, IWebhookStore webhooks, IActorAccessor accessor,
    TestSendThrottle throttle) : IRequestHandler<AdminTestChannelCommand, TestChannelResult>
{
    public async ValueTask<Result<TestChannelResult>> HandleAsync(AdminTestChannelCommand command, CancellationToken ct = default)
        => await ChannelTests.SendAsync(command.Channel, channels, email, persons, webhooks, accessor, throttle, ChannelTests.Household, ct);
}
