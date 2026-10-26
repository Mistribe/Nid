using Nid.Core.Contracts;
using UnambitiousFx.Functional;

namespace Nid.Platform.Contracts;

/// <summary>A message ready for the outside: no amount, an absolute link (notifications design §5.3).</summary>
public sealed record RenderedMessage(string Subject, string Text, string Html);

/// <summary>Who a channel writes to: the person and their address on that channel (an email address for <c>email</c>).</summary>
public sealed record ChannelTarget(PersonId Person, string Address);

/// <summary>Failure codes of a channel (notifications design §5.1).</summary>
public static class NotificationChannelCodes
{
    /// <summary>The channel could not be reached: the delivery is retried.</summary>
    public const string Unreachable = "notifications.channel_unreachable";

    /// <summary>The channel refused the message: the delivery fails at once, a retry would be refused again.</summary>
    public const string Rejected = "notifications.channel_rejected";

    /// <summary>Metadata key of a failure: the protocol status (an SMTP code) that came with it, kept beside the code in <c>last_error</c>.</summary>
    public const string StatusKey = "status";

    /// <summary>The protocol status a channel attached to its failure, null when it gave none.</summary>
    public static int? StatusOf(UnambitiousFx.Functional.Failures.Failure failure)
        => failure.Metadata.ContainsKey(StatusKey) && failure.Metadata[StatusKey] is int status ? status : null;
}

/// <summary>A way out of the application for notifications (notifications design §5.1). The first one is <c>email</c>.</summary>
public interface INotificationChannel
{
    string Name { get; }

    /// <summary>False when the installation cannot use the channel now (not configured).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Sends the message. A failure carries <see cref="NotificationChannelCodes.Unreachable"/> (retry) or
    /// <see cref="NotificationChannelCodes.Rejected"/> (no retry); its message never holds the content.
    /// </summary>
    Task<Result> SendAsync(RenderedMessage message, ChannelTarget target, CancellationToken ct);
}
