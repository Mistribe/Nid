using System.Globalization;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;

namespace Nid.Platform.Notifications;

/// <summary>
/// A notification as it is worded. No amount, except the figures a signal was raised on when the centre asks for them (issue #81):
/// outside the application the screen it links to has the figures.
/// </summary>
public sealed record RenderedNotification(string Title, string Body, string? Link);

/// <summary>The French copy of every kind (notifications design §4). The title names the kind, the body names the item and the date.</summary>
public static class NotificationTemplates
{
    private const string UnknownName = "Élément";
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private static readonly Dictionary<string, string> Titles = new()
    {
        ["subscription_renewal"] = "Un abonnement se renouvelle",
        ["subscription_cancel_deadline"] = "Dernier jour pour résilier un contrat",
        ["subscription_expiry"] = "Un abonnement prend fin",
        ["subscription_trial_end"] = "Une période gratuite se termine",
        ["subscription_price_increase"] = "Le prix d'un abonnement augmente",
        ["subscriptions.ends_without_renewal"] = "Un abonnement s'arrête sans renouvellement",
        ["tenancy_end"] = "Un bail arrive à échéance",
        ["property_cost_due"] = "Une charge prévue arrive",
        ["loan_phase_end"] = "Une phase de crédit se termine",
        ["loan_end"] = "Un crédit se termine",
        ["rent_arrears"] = "Des loyers sont impayés",
        ["revision_due"] = "Un loyer peut être révisé",
        ["revision_lost"] = "Une révision de loyer a été perdue",
        ["irl_missing"] = "L'IRL d'une révision manque",
        ["valuation_stale"] = "Une estimation est à refaire",
        ["holding_maturity"] = "Un support arrive à échéance",
        ["holding_entitlement"] = "Des parts de SCPI entrent en jouissance",
        ["stale_value"] = "Une valeur est à mettre à jour",
        ["uc_share_below_floor"] = "La part en unités de compte est sous le plancher",
        ["income_phase_end"] = "Une phase de revenu se termine",
        ["income_gap_soon"] = "Un trou de revenus approche",
        ["childcare_statement_due"] = "Un relevé de garde est à valider",
        ["childcare_below_minimum"] = "Un contrat de garde est sous un minimum légal",
        ["fx_rate_missing"] = "Un taux de change manque",
        ["routine_due"] = "La routine du mois est à faire",
        ["month_changes"] = "Ce qui a changé depuis le mois dernier",
        ["tax_filing_deadline"] = "La déclaration de revenus est à faire",
        ["tax_new_rate"] = "Le nouveau taux de prélèvement s'applique",
        ["tax_balance_due"] = "Le solde de l'impôt est prélevé",
        ["property_tax_due"] = "La taxe foncière est à payer",
        ["access_violation"] = "Une réponse a été bloquée par la garde d'accès",
        ["token_expiring"] = "Un jeton d'accès expire bientôt",
        [AccountNotices.LoginLocked] = "Votre compte a été verrouillé",
        [AccountNotices.PasswordLinkIssued] = "Un lien de réinitialisation a été créé pour vous",
        [AccountNotices.PasswordReset] = "Votre mot de passe a été réinitialisé",
        [AccountNotices.PasswordChanged] = "Votre mot de passe a changé",
        [AccountNotices.TotpReset] = "Votre validation en deux étapes a été désactivée",
        [AccountNotices.RecoveryCodesLow] = "Il vous reste 2 codes de secours",
        ["proposal_pending"] = "Une proposition attend votre décision",
        ["private_request_refused"] = "Votre demande de passage en privé a été refusée",
        ["backup_failed"] = "La sauvegarde a échoué",
        ["job_failed"] = "Une tâche planifiée a échoué",
        ["ai.data_policy_changed"] = "Ce que voient les modèles a changé",
        ["ai.budget_changed"] = "Le budget des fournisseurs d'IA payants a changé",
        ["ai.budget_alert"] = "80 % du budget d'IA du mois est dépensé",
        ["ai.budget_reached"] = "Le budget d'IA du mois est atteint",
        ["sync_failed"] = "Une synchronisation a échoué",
        ["banking.consent_expiring"] = "Le consentement d'une banque expire bientôt",
        ["banking.consent_expired"] = "Le consentement d'une banque a expiré",
        ["banking.sync_failing"] = "La synchronisation d'une banque échoue",
        ["banking.amount_differs"] = "Montant différent du prévu",
        ["banking.charge_missing"] = "Charge attendue non prélevée",
        ["net_worth_milestone"] = "Votre patrimoine a franchi un palier",
        ["savings_pace"] = "Votre rythme d'épargne a ralenti",
        ["shortfall_ahead"] = "Un compte courant risque de manquer d'argent",
        ["idle_cash"] = "Vos comptes courants dépassent l'épargne de précaution",
        ["card_unused"] = "Une carte n'a pas assez servi ce mois-ci",
        ["unusual_spending"] = "Des dépenses sortent de l'ordinaire",
    };

    /// <summary>Issue #81: the figures a signal carries (params ending in <c>_amount</c>), named, in this order.</summary>
    private static readonly (string Param, string Name)[] Figures = [
        ("net_worth_amount", "Patrimoine"), ("step_amount", "Palier"),
        ("recent_amount", "Épargne par mois sur 3 mois"), ("average_amount", "sur 12 mois"),
        ("balance_amount", "Solde"), ("charges_amount", "Charges à venir"),
        ("above_amount", "Au-dessus de l'épargne de précaution"), ("buffer_amount", "Épargne de précaution"), ("room_amount", "Place sur les livrets"),
        ("spent_amount", "Dépensé ce mois"), ("usual_amount", "Mois habituel"), ("budget_amount", "Budget"), ("projected_amount", "Fin de mois au rythme actuel"),
    ];

    /// <summary>The investments tab a kind opens; a kind not listed opens the envelope's first tab.</summary>
    private static readonly Dictionary<string, string> InvestmentTabs = new()
    {
        ["holding_maturity"] = "holdings",
        ["holding_entitlement"] = "holdings",
        ["uc_share_below_floor"] = "holdings",
        ["shortfall_ahead"] = "operations",
    };

    /// <summary>The properties tab a kind opens; a kind not listed opens the property's first tab.</summary>
    private static readonly Dictionary<string, string> PropertyTabs = new()
    {
        ["rent_arrears"] = "rental",
        ["revision_due"] = "rental",
        ["revision_lost"] = "rental",
        ["irl_missing"] = "rental",
        ["tenancy_end"] = "rental",
        ["property_cost_due"] = "expenses",
        ["property_tax_due"] = "expenses",
        ["valuation_stale"] = "valuations",
    };

    /// <summary>The title of a kind, as the preferences screen lists it; the kind itself when it has none.</summary>
    public static string TitleOf(string kind) => Titles.GetValueOrDefault(kind, kind);

    /// <summary><paramref name="withFigures"/>: the centre's wording, the body ending with the signal's figures (« Patrimoine : 302 140 € »).</summary>
    public static RenderedNotification Render(NotificationRecord n, NotificationKind kind, IReadOnlyDictionary<ItemId, string> names, bool withFigures = false)
    {
        var text = Words(n, kind, names);
        if (!withFigures || FiguresOf(n.Params) is not { Length: > 0 } figures)
            return text;
        return text with { Body = text.Body.Length > 0 ? $"{text.Body} — {figures}" : figures };
    }

    /// <summary>
    /// « Patrimoine : 302 140 € · Palier : 300 000 € », whole units of the signal's <c>currency</c> (euros when it has none); a figure
    /// that does not parse is left out.
    /// </summary>
    private static string FiguresOf(IReadOnlyDictionary<string, string> p)
    {
        var currency = p.GetValueOrDefault("currency", "EUR") is "EUR" ? "€" : p["currency"];
        var shown = new List<string>();
        foreach (var (param, name) in Figures)
            if (p.TryGetValue(param, out var value) && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
                shown.Add($"{name} : {Math.Round(amount, 0, MidpointRounding.AwayFromZero).ToString("N0", French)}\u00a0{currency}");
        // The shortfall's tipping charges, in the centre only: a name may hold an amount (« Loyer 1 200 € »).
        if (p.TryGetValue("charges", out var charges) && charges.Length > 0)
            shown.Add($"Ce jour-là : {charges}");
        // Issue #305: the heaviest lines' bank labels, which may hold an amount.
        if (p.TryGetValue("lines", out var lines) && lines.Length > 0)
            shown.Add($"Plus grosses dépenses : {lines}");
        return string.Join(" · ", shown);
    }

    private static RenderedNotification Words(NotificationRecord n, NotificationKind kind, IReadOnlyDictionary<ItemId, string> names)
    {
        var title = Titles.GetValueOrDefault(n.Kind, kind.Kind);
        if (Notices.Contains(n.Kind))
            return new RenderedNotification(title, NoticeBody(n), NoticeLink(n)); // never an item name: they say what happened, not about what
        if (n.ItemIds.Count == 0)
            return new RenderedNotification(title, HouseholdBody(n), LinkOf(n, kind));

        var name = n.ItemIds.Count > 0 && names.TryGetValue(n.ItemIds[0], out var known) && !string.IsNullOrWhiteSpace(known) ? known : UnknownName;
        return new RenderedNotification(title, name + Suffix(n), LinkOf(n, kind));
    }

    /// <summary>The notices published by code (security, pending actions, health): they carry no name and no amount.</summary>
    private static readonly HashSet<string> Notices = [.. AccountNotices.Kinds.Select(k => k.Kind), "access_violation", "token_expiring", "proposal_pending", "private_request_refused", "backup_failed", "job_failed", "ai.data_policy_changed", "ai.budget_changed", "ai.budget_alert", "ai.budget_reached", "sync_failed", "banking.consent_expiring", "banking.consent_expired", "banking.sync_failing"];

    private static string NoticeBody(NotificationRecord n) => n.Kind switch
    {
        "access_violation" => n.Params.TryGetValue("request", out var request) && request.Length > 0 ? $"Requête : {request}" : "",
        "token_expiring" => KeyDate(n.Key, "yyyy-MM-dd") is { } on ? $"Expire le {Day(on)}" : "",
        "proposal_pending" => "Une décision est attendue",
        "private_request_refused" => "L'autre adulte n'a pas accepté : l'élément reste partagé.",
        "backup_failed" => BackupReason(n.Params),
        "job_failed" => n.Params.TryGetValue("job", out var job) && job.Length > 0 ? $"Tâche : {job}" : "",
        "ai.data_policy_changed" => DataPolicyChange(n.Params),
        "ai.budget_changed" => "Un adulte a changé le plafond mensuel des fournisseurs d'IA payants.",
        "ai.budget_alert" => "Au-delà du plafond, les appels payants s'arrêtent jusqu'au 1er du mois prochain.",
        "ai.budget_reached" => "Les appels payants sont arrêtés jusqu'au 1er du mois prochain ; les workers du foyer continuent.",
        "sync_failed" => n.Params.TryGetValue("source", out var source) && source.Length > 0 ? $"Source : {source}" : "",
        "banking.consent_expiring" => n.DueOn is { } on ? $"{Bank(n)} — expire le {Day(on)}" : Bank(n),
        "banking.consent_expired" => n.DueOn is { } on ? $"{Bank(n)} — expiré le {Day(on)}" : Bank(n),
        "banking.sync_failing" => Bank(n),
        AccountNotices.LoginLocked => "Après plusieurs essais de mot de passe incorrects. Si ce n'était pas vous, prévenez un administrateur.",
        AccountNotices.PasswordLinkIssued => "Un administrateur peut ainsi choisir un nouveau mot de passe pour votre compte. Si vous ne l'avez pas demandé, parlez-en avec lui.",
        AccountNotices.PasswordReset => "Vos autres sessions sont fermées. Si ce n'était pas vous, prévenez un administrateur.",
        AccountNotices.PasswordChanged => "Vos autres sessions sont fermées. Si ce n'était pas vous, prévenez un administrateur.",
        AccountNotices.TotpReset => "Un administrateur l'a désactivée depuis le serveur. Réactivez-la depuis la page Sécurité.",
        AccountNotices.RecoveryCodesLow => "Créez-en de nouveaux depuis la page Sécurité avant d'en manquer.",
        _ => "",
    };

    private static string Bank(NotificationRecord n) => n.Params.GetValueOrDefault("bank") ?? "";

    /// <summary>Why the backup is unhealthy (<see cref="BackupState.Reason"/>): a failed step is named as the script names it.</summary>
    private static string BackupReason(IReadOnlyDictionary<string, string> p) => p.GetValueOrDefault("reason") switch
    {
        null or "" => "Consultez l'état du système",
        "too_old" => p.TryGetValue("hours", out var hours) && hours.Length > 0
            ? $"Aucune sauvegarde réussie depuis plus de {hours} h" : "Aucune sauvegarde réussie depuis trop longtemps",
        "remote" => "La copie distante a échoué",
        "unreadable" => "L'état de la sauvegarde est illisible",
        "configuration" => "La configuration de la sauvegarde est invalide",
        var step => $"Étape en échec : {step}",
    };

    /// <summary>
    /// The notice's own screen. <c>access_violation</c> opens the security journal for an admin, which the notifier tells in
    /// <c>Params["admin"]</c> because only admins may see it; anyone else lands on the home page.
    /// </summary>
    private static string NoticeLink(NotificationRecord n) => n.Kind switch
    {
        "access_violation" => n.Params.GetValueOrDefault("admin") == "1" ? "/admin/security" : "/",
        "token_expiring" => "/security",
        _ when AccountNotices.Kinds.Any(k => k.Kind == n.Kind) => "/security",
        "banking.consent_expiring" or "banking.consent_expired" or "banking.sync_failing" => "/settings/banks",
        "proposal_pending" or "private_request_refused" => $"/proposals/{n.Key[(n.Key.LastIndexOf(':') + 1)..]}",
        "backup_failed" => "/admin/system",
        "job_failed" => n.Params.GetValueOrDefault("job") is { Length: > 0 } job
            ? job.StartsWith("ai.", StringComparison.Ordinal) ? "/settings/ai/jobs?status=dead" : $"/admin/jobs/{job}"
            : "/admin/jobs",
        "ai.data_policy_changed" => "/settings/ai",
        "ai.budget_changed" or "ai.budget_alert" or "ai.budget_reached" => "/settings/ai",
        "sync_failed" => n.Params.GetValueOrDefault("source") is "fx" or "catalog" or "prices" or "irl" or "inflation" ? $"/admin/{n.Params["source"]}" : "/admin/system",
        _ => "/",
    };

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
            _ => "",
        };
    }

    /// <summary>What a row about no item says: a currency code is not an amount, and there is no name to give.</summary>
    private static string HouseholdBody(NotificationRecord n) => n.Kind switch
    {
        "fx_rate_missing" when n.Params.TryGetValue("currency", out var currency) && currency.Length > 0 => $"Devise {currency}",
        "routine_due" => "Des valeurs sont à mettre à jour",
        "month_changes" => MonthChanges(n.Params),
        "tax_filing_deadline" when n.DueOn is { } on => n.Params.GetValueOrDefault("note") == "department_unknown"
            ? $"En ligne avant le {Day(on)}, la date la plus tôt : département non renseigné" : $"En ligne avant le {Day(on)}",
        "tax_new_rate" when n.DueOn is { } on => $"À partir du {Day(on)}",
        "tax_balance_due" when n.DueOn is { } on => $"À partir du {Day(on)}",
        "shortfall_ahead" when n.DueOn is { } on => n.Params.TryGetValue("account", out var account) ? $"Le {Day(on)} — {account}" : $"Le {Day(on)}",
        "card_unused" when n.DueOn is { } on => CardUnused(n.Params, on),
        "idle_cash" => "Au-dessus de l'épargne de précaution depuis 4 semaines" + n.Params.GetValueOrDefault("reason") switch
        {
            "room" => ", un livret a de la place",
            "goal_behind" => ", un objectif est en retard",
            "room_goal_behind" => ", un livret a de la place et un objectif est en retard",
            _ => "",
        },
        "unusual_spending" => $"{(n.Params.GetValueOrDefault("name") is { Length: > 0 } name ? name : "Une catégorie")} : " + n.Params.GetValueOrDefault("reason") switch
        {
            "budget_over" => "budget dépassé",
            "budget_pace" => "le budget sera dépassé au rythme actuel",
            _ => "bien au-dessus du mois habituel",
        },
        _ => "",
    };

    private static readonly (string Param, string Name)[] MonthFigures = [("net_worth", "Patrimoine"), ("commitments", "Engagements"), ("income", "Revenus")];

    /// <summary>Issue #415: the causes of a month's change, named as on the dashboard (web/src/locales/fr.json, dashboard.changes.cause).</summary>
    private static readonly Dictionary<string, string> MonthCauses = new()
    {
        ["investments.paid_in"] = "versements sur les placements",
        ["investments.value"] = "valeur des placements",
        ["real_estate.paid_in"] = "capital remboursé",
        ["real_estate.value"] = "valeur de l'immobilier",
        ["real_estate.moved"] = "achats et ventes immobiliers",
        ["subscriptions"] = "abonnements",
        ["loans"] = "crédits",
        ["property_costs"] = "charges des biens",
        ["tax"] = "impôts",
        ["childcare"] = "garde d'enfants",
        ["income"] = "sources de revenu",
        ["rents"] = "loyers",
    };

    /// <summary>
    /// Issue #415: « Patrimoine en hausse — valeur des placements ↑, capital remboursé ↑ · Engagements en baisse — abonnements ↓ »; the
    /// figures stay on the dashboard (N6). Params: « up|key+|key- » per figure that moved.
    /// </summary>
    private static string MonthChanges(IReadOnlyDictionary<string, string> p)
        => string.Join(" · ", MonthFigures.Where(f => p.ContainsKey(f.Param)).Select(f =>
        {
            var parts = p[f.Param].Split('|');
            var causes = parts.Skip(1).Where(c => c.Length > 1)
                .Select(c => $"{MonthCauses.GetValueOrDefault(c[..^1], "autres")} {(c[^1] == '+' ? "↑" : "↓")}")
                .ToList();
            var line = $"{f.Name} en {(parts[0] == "up" ? "hausse" : "baisse")}";
            return causes.Count == 0 ? line : $"{line} — {string.Join(", ", causes)}";
        }));

    private static string Suffix(NotificationRecord n) => n.Kind switch
    {
        "rent_arrears" when KeyDate(n.Key, "yyyy-MM") is { } month => $" — depuis {month.ToString("MMMM yyyy", French)}",
        "childcare_statement_due" when KeyDate(n.Key, "yyyy-MM") is { } month => $" — relevé de {month.ToString("MMMM yyyy", French)}",
        "childcare_below_minimum" when (n.DueOn ?? KeyDate(n.Key, "yyyy-MM-dd")) is { } on => $" — minimum en vigueur depuis le {Day(on)}",
        "valuation_stale" when (n.DueOn ?? KeyDate(n.Key, "yyyy-MM-dd")) is { } on => $" — estimée le {Day(on)}",
        "stale_value" when (n.DueOn ?? KeyDate(n.Key, "yyyy-MM-dd")) is { } on => $" — valeur du {Day(on)}",
        "uc_share_below_floor" when (n.DueOn ?? KeyDate(n.Key, "yyyy-MM-dd")) is { } on => $" — relevé du {Day(on)}",
        "subscriptions.ends_without_renewal" or "revision_due" or "revision_lost" or "irl_missing"
            when (n.DueOn ?? KeyDate(n.Key, "yyyy-MM-dd")) is { } on => $" — le {Day(on)}",
        _ when n.DueOn is { } on => $" — le {Day(on)}",
        _ => "",
    };

    /// <summary>« 1er décembre 2027 », « 31 mars 2027 ».</summary>
    /// <summary>#362: « Carte Visa •••• 1234 : aucun paiement ce mois-ci, 1 attendu. Utilisez-la avant le 30 novembre. » The masked number only.</summary>
    private static string CardUnused(IReadOnlyDictionary<string, string> p, LocalDate by)
    {
        var name = string.Join(" ", new[] { p.GetValueOrDefault("card")?.Trim(), p.GetValueOrDefault("number") is { Length: > 0 } last4 ? $"•••• {last4}" : null }
            .Where(s => !string.IsNullOrEmpty(s)));
        var count = p.GetValueOrDefault("count") is "0" or null ? "aucun paiement" : $"{p["count"]} paiement{(p["count"] == "1" ? "" : "s")}";
        var min = p.GetValueOrDefault("min") ?? "1";
        return $"{(name.Length > 0 ? name : "Une carte")} : {count} ce mois-ci, {min} attendu{(min == "1" ? "" : "s")}. Utilisez-la avant le {Day(by)}.";
    }

    internal static string Day(LocalDate date)
        => (date.Day == 1 ? "1er" : date.Day.ToString(French)) + " " + date.ToString("MMMM yyyy", French);

    /// <summary>The date a key ends with (<see cref="SignalKeys"/>): the month of arrears, the day of a valuation.</summary>
    private static LocalDate? KeyDate(string key, string format)
    {
        var last = key[(key.LastIndexOf(':') + 1)..];
        return LocalDatePattern.CreateWithInvariantCulture(format).Parse(last) is { Success: true } parsed ? parsed.Value : null;
    }

    /// <summary>Issue #305: « Opérations » on the lines the category's figure counts that month; the spending page without them.</summary>
    private static string SpendingLink(IReadOnlyDictionary<string, string> p)
        => Guid.TryParse(p.GetValueOrDefault("category"), out var category) && YearMonthPattern.Iso.Parse(p.GetValueOrDefault("month") ?? "") is { Success: true } month
            ? $"/spending?tab=operations&category={category:D}&from={month.Value.ToDateInterval().Start:yyyy-MM-dd}&to={month.Value.ToDateInterval().End:yyyy-MM-dd}&state=counted"
            : "/spending";

    private static string? LinkOf(NotificationRecord n, NotificationKind kind)
    {
        if (n.ItemIds.Count == 0)
            return n.Kind switch { "unusual_spending" => SpendingLink(n.Params), "fx_rate_missing" or "month_changes" or "net_worth_milestone" or "savings_pace" => "/dashboard", "idle_cash" => "/investments", "routine_due" => "/routine", "shortfall_ahead" or "card_unused" => "/settings/banks", "tax_filing_deadline" or "tax_new_rate" => "/tax", "tax_balance_due" => "/tax?tab=withholding", _ => "/" };
        var id = n.ItemIds[0].Value.ToString("D");
        // The banking findings name the page of their item in "module", the item being any module's.
        return (kind.Module == "banking" ? n.Params.GetValueOrDefault("module") : kind.Module) switch
        {
            "subscriptions" => $"/subscriptions/{id}",
            "loans" => $"/loans/{id}",
            "properties" => PropertyTabs.TryGetValue(n.Kind, out var tab) ? $"/properties/{id}?tab={tab}" : $"/properties/{id}",
            "investments" => InvestmentTabs.TryGetValue(n.Kind, out var tab) ? $"/investments/{id}?tab={tab}" : $"/investments/{id}",
            "income" => $"/income/{id}",
            "childcare" => n.Params.TryGetValue("month", out var month) ? $"/childcare/{id}/statements/{month}" : $"/childcare/{id}",
            _ => "/",
        };
    }
}
