using Nid.Platform.Contracts;

namespace Nid.Platform.Notifications;

/// <summary>Platform's kinds published through <see cref="INotifier"/>: the access guard, the scheduled jobs and the local accounts.</summary>
public sealed class PlatformNotificationKinds : IPublishedNotificationKinds
{
    public IReadOnlyList<NotificationKind> Kinds { get; } =
    [
        new("access_violation", NotificationFamilies.Security, NotificationLevel.Critical, "platform", null)
        {
            // Only admins may see the security journal: the notifier tells them in Params["admin"]; anyone else lands on the home page.
            Template = new("Une réponse a été bloquée par la garde d'accès")
            {
                Notice = true,
                Body = f => f.Params.TryGetValue("request", out var request) && request.Length > 0 ? $"Requête : {request}" : "",
                Link = f => f.Params.GetValueOrDefault("admin") == "1" ? "/admin/security" : "/",
            },
        },
        new("job_failed", NotificationFamilies.Health, NotificationLevel.Action, "platform", null)
        {
            Template = new("Une tâche planifiée a échoué")
            {
                Notice = true,
                Body = f => f.Params.TryGetValue("job", out var job) && job.Length > 0 ? $"Tâche : {job}" : "",
                Link = f => f.Params.GetValueOrDefault("job") is { Length: > 0 } job
                    ? job.StartsWith("ai.", StringComparison.Ordinal) ? "/admin/ai/jobs?status=dead" : $"/admin/jobs/{job}"
                    : "/admin/jobs",
            },
        },
        .. AccountNotices.Kinds,
    ];
}
