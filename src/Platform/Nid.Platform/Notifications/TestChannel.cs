using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Notifications;

/// <summary>
/// Sends a test message to the caller on a channel (notifications design §6.1). <paramref name="Sent"/> is false when the channel
/// refused it: <paramref name="Error"/> then holds the failure code, never a message.
/// </summary>
public sealed record TestChannelResult(bool Sent, string? Error);

/// <summary>« Nid : message de test » to the caller's own address. Unknown channel or no email available: a field error on <c>channel</c>.</summary>
public sealed record TestChannelCommand(string Channel) : ICommand<TestChannelResult>;

internal static class ChannelTests
{
    public static RenderedMessage TestMessage(string what) => new(
        "Nid : message de test",
        $"Ceci est un message de test envoyé depuis Nid. Si vous le lisez, les notifications {what} fonctionnent.",
        $"<p><strong>Message de test</strong></p><p>Ceci est un message de test envoyé depuis Nid. Si vous le lisez, les notifications {what} fonctionnent.</p>");

    public static readonly string[] Personal = [ChannelAccess.Email, ChannelAccess.DiscordPerson];
    public static readonly string[] Household = [ChannelAccess.Email, ChannelAccess.DiscordHousehold];

    public static async Task<Result<TestChannelResult>> SendAsync(
        string channelName, IEnumerable<INotificationChannel> channels, IEmailAvailability email, IPersonRegistry persons,
        IWebhookStore webhooks, IActorAccessor accessor, TestSendThrottle throttle, string[] allowed, CancellationToken ct)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Failure<TestChannelResult>(NotificationAccess.Unauthorized());

        var name = allowed.FirstOrDefault(a => string.Equals(a, channelName, StringComparison.OrdinalIgnoreCase));
        if (name is null)
            return Refused(NotificationCodes.UnknownChannel, "This notification channel does not exist.");

        var channel = channels.FirstOrDefault(c => c.Name == name);
        string? address;
        if (name == ChannelAccess.Email)
        {
            address = (await persons.FindAsync(me, ct))?.Email;
            if (!email.IsConfigured || channel is not { IsAvailable: true } || string.IsNullOrWhiteSpace(address))
                return Refused(NotificationCodes.EmailUnavailable, "Email needs a configured server and an address on your profile.");
        }
        else
        {
            var owner = name == ChannelAccess.DiscordHousehold ? null : (PersonId?)me;
            address = ChannelAccess.WebhookAddress(await webhooks.FindAsync(owner, ct));
            if (channel is not { IsAvailable: true } || address is null)
                return Refused(NotificationCodes.DiscordUnavailable, name == ChannelAccess.DiscordHousehold
                    ? "Discord needs a working household webhook."
                    : "Discord needs a working webhook on your profile.");
        }

        if (!throttle.TryTake(me, name))
            return Result.Failure<TestChannelResult>(new Failure(NotificationCodes.TestThrottled, "Wait a few seconds before another test.", null));

        try
        {
            var result = await channel.SendAsync(TestMessage(name == ChannelAccess.Email ? "par email" : "Discord"), new ChannelTarget(me, address), ct);
            if (!result.TryGetFailure(out var failure))
                return Result.Success(new TestChannelResult(true, null));
            if (failure!.Code == NotificationChannelCodes.Broken)
                await webhooks.MarkBrokenAsync(name == ChannelAccess.DiscordHousehold ? null : me, address, ct);
            return Result.Success(new TestChannelResult(false, failure.Code));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result.Success(new TestChannelResult(false, NotificationChannelCodes.Unreachable));
        }
    }

    private static Result<TestChannelResult> Refused(string code, string message)
        => Result.Failure<TestChannelResult>(FieldErrors.At(new Failure(code, message, null), "channel"));
}

[RequestHandler<TestChannelCommand, TestChannelResult>]
public sealed class TestChannelHandler(
    IEnumerable<INotificationChannel> channels, IEmailAvailability email, IPersonRegistry persons, IWebhookStore webhooks, IActorAccessor accessor,
    TestSendThrottle throttle) : IRequestHandler<TestChannelCommand, TestChannelResult>
{
    public async ValueTask<Result<TestChannelResult>> HandleAsync(TestChannelCommand command, CancellationToken ct = default)
        => await ChannelTests.SendAsync(command.Channel, channels, email, persons, webhooks, accessor, throttle, ChannelTests.Personal, ct);
}
