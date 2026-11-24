using Nid.Platform.Contracts;

namespace Nid.Platform.Notifications;

/// <summary>
/// The kinds published through <see cref="INotifier"/>, which no source evaluates. The catalogue reads them next to the sources' kinds.
/// Issue #514: Core's and Ai's published kinds are still declared here, with their copy, until published kinds get a contract of their own.
/// </summary>
public static class PlatformNotificationKinds
{
    private const string AiSettings = "/settings/ai";

    public static readonly IReadOnlyList<NotificationKind> All =
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
        new("proposal_pending", NotificationFamilies.Action, NotificationLevel.Action, "core", null)
        {
            Personal = true,
            Template = new("Une proposition attend votre décision") { Notice = true, Body = _ => "Une décision est attendue", Link = Proposal },
        },
        // #384: the other adult refused to make an item private; told to the owner who asked.
        new("private_request_refused", NotificationFamilies.Action, NotificationLevel.Info, "core", null)
        {
            Personal = true,
            Template = new("Votre demande de passage en privé a été refusée")
            {
                Notice = true, Body = _ => "L'autre adulte n'a pas accepté : l'élément reste partagé.", Link = Proposal,
            },
        },
        new("job_failed", NotificationFamilies.Health, NotificationLevel.Action, "platform", null)
        {
            Template = new("Une tâche planifiée a échoué")
            {
                Notice = true,
                Body = f => f.Params.TryGetValue("job", out var job) && job.Length > 0 ? $"Tâche : {job}" : "",
                Link = f => f.Params.GetValueOrDefault("job") is { Length: > 0 } job
                    ? job.StartsWith("ai.", StringComparison.Ordinal) ? "/settings/ai/jobs?status=dead" : $"/admin/jobs/{job}"
                    : "/admin/jobs",
            },
        },
        // #475: what may reach a remote or a safe model worker changed; every adult is told (docs/ai/README.md §12).
        new("ai.data_policy_changed", NotificationFamilies.Security, NotificationLevel.Critical, "ai", null)
        {
            Template = new("Ce que voient les modèles a changé") { Notice = true, Body = f => DataPolicyChange(f.Params), Link = _ => AiSettings },
        },
        // #488: the budget for paid model providers: changed, 80 % spent, reached (blocked until the 1st).
        new("ai.budget_changed", NotificationFamilies.Security, NotificationLevel.Info, "ai", null)
        {
            Template = new("Le budget des fournisseurs d'IA payants a changé")
            {
                Notice = true, Body = _ => "Un adulte a changé le plafond mensuel des fournisseurs d'IA payants.", Link = _ => AiSettings,
            },
        },
        new("ai.budget_alert", NotificationFamilies.Action, NotificationLevel.Action, "ai", null)
        {
            Template = new("80 % du budget d'IA du mois est dépensé")
            {
                Notice = true, Body = _ => "Au-delà du plafond, les appels payants s'arrêtent jusqu'au 1er du mois prochain.", Link = _ => AiSettings,
            },
        },
        new("ai.budget_reached", NotificationFamilies.Action, NotificationLevel.Critical, "ai", null)
        {
            Template = new("Le budget d'IA du mois est atteint")
            {
                Notice = true,
                Body = _ => "Les appels payants sont arrêtés jusqu'au 1er du mois prochain ; les workers du foyer continuent.",
                Link = _ => AiSettings,
            },
        },
        .. AccountNotices.Kinds,
    ];

    private static string Proposal(NotificationFacts f) => $"/proposals/{f.Key[(f.Key.LastIndexOf(':') + 1)..]}";

    private static string Use(IReadOnlyDictionary<string, string> p) => p.GetValueOrDefault("use") switch
    {
        "chat" => "assistant",
        "embed" => "recherche par le sens",
        "decide" => "suggestions",
        "extract" => "lecture des documents",
        var other => other ?? "",
    };

    private static string DataPolicyChange(IReadOnlyDictionary<string, string> p)
    {
        var worker = p.GetValueOrDefault("worker") is { Length: > 0 } name ? $"« {name} »" : "Un worker";
        return p.GetValueOrDefault("change") switch
        {
            "remote_on" => "Les workers distants peuvent désormais recevoir les données du foyer.",
            "remote_off" => "Les workers distants ne reçoivent plus les données du foyer.",
            "safe_on" => $"{worker} peut désormais recevoir les documents « local seulement ».",
            "safe_off" => $"{worker} ne reçoit plus les documents « local seulement ».",
            "cloud_on" => $"L'usage « {Use(p)} » peut désormais passer par un fournisseur cloud activé.",
            "cloud_off" => $"L'usage « {Use(p)} » ne passe plus que par les workers du foyer.",
            "cloud_profile_on" => $"Le fournisseur cloud « {p.GetValueOrDefault("profile")} » est activé : il peut recevoir les demandes des usages ouverts au cloud.",
            "cloud_profile_off" => $"Le fournisseur cloud « {p.GetValueOrDefault("profile")} » est désactivé.",
            "assistant_cloud_on" => "Un adulte a activé l'assistant pour lui alors qu'il peut passer par un fournisseur cloud : ses propres chiffres peuvent y partir, jamais les éléments privés de l'autre adulte.",
            _ => "",
        };
    }
}
