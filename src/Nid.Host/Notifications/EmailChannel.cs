using System.Net.Sockets;
using MailKit.Net.Smtp;
using MimeKit;
using Nid.Platform.Contracts;
using Nid.Platform.Notifications;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Host.Notifications;

/// <summary>The email channel (notifications design §5.3), over <see cref="ISmtpTransport"/>.</summary>
public sealed class EmailChannel(SmtpOptions options, ISmtpTransport transport) : INotificationChannel
{
    public string Name => "email";

    public bool IsAvailable => options.IsConfigured;

    public async Task<Result> SendAsync(RenderedMessage message, ChannelTarget target, CancellationToken ct)
    {
        MimeMessage mail;
        try
        {
            mail = new MimeMessage
            {
                Subject = message.Subject,
                Body = new BodyBuilder { TextBody = message.Text, HtmlBody = message.Html }.ToMessageBody(),
            };
            mail.From.Add(MailboxAddress.Parse(options.From!));
            mail.To.Add(MailboxAddress.Parse(target.Address));
        }
        catch (ParseException)
        {
            // An address nobody can write to: a retry would be refused the same way.
            return Fail(NotificationChannelCodes.Rejected, null);
        }

        try
        {
            await transport.SendAsync(mail, ct);
            return Result.Success();
        }
        catch (SmtpCommandException e)
        {
            var status = (int)e.StatusCode;
            return Fail(status >= 500 ? NotificationChannelCodes.Rejected : NotificationChannelCodes.Unreachable, status);
        }
        catch (SmtpProtocolException)
        {
            return Fail(NotificationChannelCodes.Rejected, null);
        }
        catch (Exception e) when (e is MailKit.Security.AuthenticationException or MailKit.Security.SslHandshakeException)
        {
            // The server refused our credentials or the TLS handshake failed: every message would fail the same way, the admin has to fix the settings.
            return Fail(NotificationChannelCodes.Rejected, null);
        }
        catch (Exception e) when (e is SocketException or IOException or TimeoutException
                                      || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return Fail(NotificationChannelCodes.Unreachable, null);
        }
    }

    // The message holds no address, subject or body: the code and the status are all that is kept.
    private static Result Fail(string code, int? status)
        => Result.Failure(new Failure(
            code, code, status is { } s ? new Dictionary<string, object?> { [NotificationChannelCodes.StatusKey] = s } : null));
}
