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
        // #475: what may reach a remote or a safe model worker changed; every adult is told (docs/ai/README.md §12).
        new("ai.data_policy_changed", NotificationFamilies.Security, NotificationLevel.Critical, "ai", null),
        // #488: the budget for paid model providers: changed, 80 % spent, reached (blocked until the 1st).
        new("ai.budget_changed", NotificationFamilies.Security, NotificationLevel.Info, "ai", null),
        new("ai.budget_alert", NotificationFamilies.Action, NotificationLevel.Action, "ai", null),
        new("ai.budget_reached", NotificationFamilies.Action, NotificationLevel.Critical, "ai", null),
        .. AccountNotices.Kinds,
    ];
}
