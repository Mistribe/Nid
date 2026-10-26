using MimeKit;

namespace Nid.Host.Notifications;

/// <summary>How the connection to the SMTP server is secured; the names are the values of <c>Nid:Smtp:Security</c>.</summary>
public enum SmtpSecurity
{
    /// <summary>Plain connection upgraded with STARTTLS (port 587).</summary>
    StartTls,

    /// <summary>TLS from the first byte (port 465).</summary>
    SslOnConnect,

    /// <summary>No encryption: a relay on a trusted network only.</summary>
    None,
}

/// <summary>
/// The <c>Nid:Smtp</c> section (<c>Nid__Smtp__Host</c>…). The email channel exists only when <see cref="Host"/> is set, and then
/// <see cref="From"/> is required (notifications design §5.3).
/// </summary>
public sealed class SmtpOptions
{
    public const string Section = "Nid:Smtp";

    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public string? User { get; set; }

    public string? Password { get; set; }

    /// <summary>The sender, an address or <c>Nid &lt;nid@example.org&gt;</c>.</summary>
    public string? From { get; set; }

    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    /// <summary>The server and the sender are both set: the channel can send.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From);

    /// <summary>
    /// Stops the startup, with a message that names the setting, on an incomplete or unusable configuration. <paramref name="publicUrl"/>
    /// is <c>Auth:PublicUrl</c>: the links of an email are absolute, so it is required once the channel exists.
    /// </summary>
    public void Validate(string? publicUrl)
    {
        if (string.IsNullOrWhiteSpace(Host))
            return;
        if (string.IsNullOrWhiteSpace(From))
            throw new InvalidOperationException($"{Section}:From is required when {Section}:Host is set (Nid__Smtp__From).");
        if (!MailboxAddress.TryParse(From, out _))
            throw new InvalidOperationException($"{Section}:From is not an email address.");
        if (Port is < 1 or > 65535)
            throw new InvalidOperationException($"{Section}:Port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(publicUrl))
            throw new InvalidOperationException($"Auth:PublicUrl is required when {Section}:Host is set: the links of an email are absolute (Auth__PublicUrl).");
    }
}
