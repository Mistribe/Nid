using Nid.Platform.Contracts;

namespace Nid.Platform.Notifications;

/// <summary>
/// The kinds published through <see cref="INotifier"/>, which no source evaluates. The catalogue reads them next to the sources' kinds.
/// </summary>
public static class PlatformNotificationKinds
{
    public static readonly IReadOnlyList<NotificationKind> All =
    [
        new("access_violation", NotificationFamilies.Security, NotificationLevel.Critical, "platform", null),
        new("proposal_pending", NotificationFamilies.Action, NotificationLevel.Action, "core", null),
        new("job_failed", NotificationFamilies.Health, NotificationLevel.Action, "platform", null),
    ];
}
