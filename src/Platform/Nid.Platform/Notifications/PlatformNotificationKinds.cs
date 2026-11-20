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
        // #384: the other adult refused to make an item private; told to the owner who asked.
        new("private_request_refused", NotificationFamilies.Action, NotificationLevel.Info, "core", null),
        new("job_failed", NotificationFamilies.Health, NotificationLevel.Action, "platform", null),
        .. AccountNotices.Kinds,
    ];
}
