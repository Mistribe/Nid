using Nid.Platform.Contracts;

namespace Nid.Ai.Application;

/// <summary>#644: Ai's kinds published through <see cref="INotifier"/>: what the models may see and the paid providers' budget.</summary>
public sealed class AiNotificationKinds : IPublishedNotificationKinds
{
    private const string AiSettings = "/settings/ai";

    public IReadOnlyList<NotificationKind> Kinds { get; } =
    [
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
                Notice = true, Body = _ => "Un administrateur a changé le plafond mensuel des fournisseurs d'IA payants.", Link = _ => AiSettings,
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
    ];

    private static string Use(IReadOnlyDictionary<string, string> p) => p.GetValueOrDefault("use") switch
    {
        "chat" => "assistant",
        "embed" => "recherche par le sens",
        "decide" => "suggestions",
        "extract" => "lecture des documents",
        var other => other ?? "",
    };

    /// <summary>#556: the document types of <c>allowed_kinds</c>, and the bank labels (#812), in words.</summary>
    private static string CloudKinds(string kinds) => string.Join(", ", kinds.Split(',').Select(k => k switch
    {
        "statement" => "relevés",
        "amortization_table" => "tableaux d'amortissement",
        "invoice" => "factures",
        "contract" => "contrats",
        "bank_labels" => "libellés bancaires",
        _ => k,
    }));

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
            "allowed_kinds" => p.GetValueOrDefault("kinds") is { Length: > 0 } kinds
                ? $"Ces données peuvent désormais partir vers un fournisseur cloud activé, si chaque adulte concerné l'autorise : {CloudKinds(kinds)}."
                : "Plus aucun document ni libellé bancaire ne part vers un fournisseur cloud.",
            "assistant_cloud_on" => "Un adulte a activé l'assistant pour lui alors qu'il peut passer par un fournisseur cloud : ses propres chiffres peuvent y partir, jamais les éléments privés de l'autre adulte.",
            _ => "",
        };
    }
}
