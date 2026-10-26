namespace Nid.Platform.Notifications;

/// <summary>Whether the email channel exists on this installation: SMTP is configured (notifications design §5.3).</summary>
public interface IEmailAvailability
{
    bool IsConfigured { get; }

    /// <summary>The SMTP host, for the admin status; null when none is configured.</summary>
    string? Host => null;
}

/// <summary>No SMTP configured: the channel does not exist. The host replaces it when <c>Nid:Smtp:Host</c> is set.</summary>
public sealed class NoEmailAvailability : IEmailAvailability
{
    public bool IsConfigured => false;
}
