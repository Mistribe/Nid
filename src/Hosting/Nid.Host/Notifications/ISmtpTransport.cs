using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Nid.Host.Notifications;

/// <summary>Hands one message to the SMTP server. A seam for tests: the failures are MailKit's, <see cref="EmailChannel"/> classifies them.</summary>
public interface ISmtpTransport
{
    Task SendAsync(MimeMessage message, CancellationToken ct);
}

/// <summary>One connection per message: the volume is a household's, a pooled connection would only add states to get wrong.</summary>
public sealed class MailKitSmtpTransport(SmtpOptions options) : ISmtpTransport
{
    private static readonly int TimeoutMilliseconds = (int)TimeSpan.FromSeconds(30).TotalMilliseconds;

    public async Task SendAsync(MimeMessage message, CancellationToken ct)
    {
        using var client = new SmtpClient { Timeout = TimeoutMilliseconds };
        await client.ConnectAsync(options.Host!, options.Port, options.Security switch
        {
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        }, ct);
        if (!string.IsNullOrEmpty(options.User))
            await client.AuthenticateAsync(options.User, options.Password ?? "", ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}
