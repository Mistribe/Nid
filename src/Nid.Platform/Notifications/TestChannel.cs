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
    public static readonly RenderedMessage TestMessage = new(
        "Nid : message de test",
        "Ceci est un message de test envoyé depuis Nid. Si vous le lisez, les notifications par email fonctionnent.",
        "<p><strong>Message de test</strong></p><p>Ceci est un message de test envoyé depuis Nid. Si vous le lisez, les notifications par email fonctionnent.</p>");

    public static async Task<Result<TestChannelResult>> SendAsync(
        string channelName, IEnumerable<INotificationChannel> channels, IEmailAvailability email, IPersonRegistry persons,
        IActorAccessor accessor, CancellationToken ct)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Failure<TestChannelResult>(NotificationAccess.Unauthorized());

        if (!string.Equals(channelName, ChannelAccess.Email, StringComparison.OrdinalIgnoreCase))
            return Refused(NotificationCodes.UnknownChannel, "This notification channel does not exist.");

        var channel = channels.FirstOrDefault(c => c.Name == ChannelAccess.Email);
        var address = (await persons.FindAsync(me, ct))?.Email;
        if (!email.IsConfigured || channel is not { IsAvailable: true } || string.IsNullOrWhiteSpace(address))
            return Refused(NotificationCodes.EmailUnavailable, "Email needs a configured server and an address on your profile.");

        try
        {
            var result = await channel.SendAsync(TestMessage, new ChannelTarget(me, address), ct);
            return Result.Success(result.TryGetFailure(out var failure) ? new TestChannelResult(false, failure!.Code) : new TestChannelResult(true, null));
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
    IEnumerable<INotificationChannel> channels, IEmailAvailability email, IPersonRegistry persons, IActorAccessor accessor)
    : IRequestHandler<TestChannelCommand, TestChannelResult>
{
    public async ValueTask<Result<TestChannelResult>> HandleAsync(TestChannelCommand command, CancellationToken ct = default)
        => await ChannelTests.SendAsync(command.Channel, channels, email, persons, accessor, ct);
}
