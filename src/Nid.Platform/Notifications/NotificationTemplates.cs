using System.Globalization;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;

namespace Nid.Platform.Notifications;

/// <summary>A notification as the centre shows it. Never carries an amount: the screen it links to has the figures.</summary>
public sealed record RenderedNotification(string Title, string Body, string? Link);

/// <summary>The French copy of every kind (notifications design §4). The title names the kind, the body names the item and the date.</summary>
public static class NotificationTemplates
{
    private const string UnknownName = "Élément";
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private static readonly Dictionary<string, string> Titles = new()
    {
        ["subscription_renewal"] = "Un abonnement se renouvelle",
        ["subscription_expiry"] = "Un abonnement prend fin",
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
        ["stale_value"] = "Une valeur est à mettre à jour",
        ["uc_share_below_floor"] = "La part en unités de compte est sous le plancher",
        ["income_phase_end"] = "Une phase de revenu se termine",
        ["income_gap_soon"] = "Un trou de revenus approche",
        ["fx_rate_missing"] = "Un taux de change manque",
        ["routine_due"] = "La routine du mois est à faire",
        ["access_violation"] = "Une réponse a été bloquée par la garde d'accès",
        ["token_expiring"] = "Un jeton d'accès expire bientôt",
        ["proposal_pending"] = "Une proposition attend votre décision",
        ["backup_failed"] = "La sauvegarde a échoué",
        ["job_failed"] = "Une tâche planifiée a échoué",
        ["sync_failed"] = "Une synchronisation a échoué",
        ["banking.consent_expiring"] = "Le consentement d'une banque expire bientôt",
        ["banking.consent_expired"] = "Le consentement d'une banque a expiré",
        ["banking.sync_failing"] = "La synchronisation d'une banque échoue",
    };

    /// <summary>The investments tab a kind opens; a kind not listed opens the envelope's first tab.</summary>
    private static readonly Dictionary<string, string> InvestmentTabs = new()
    {
        ["holding_maturity"] = "holdings",
        ["uc_share_below_floor"] = "holdings",
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
        ["valuation_stale"] = "valuations",
    };

    /// <summary>The title of a kind, as the preferences screen lists it; the kind itself when it has none.</summary>
    public static string TitleOf(string kind) => Titles.GetValueOrDefault(kind, kind);

    public static RenderedNotification Render(NotificationRecord n, NotificationKind kind, IReadOnlyDictionary<ItemId, string> names)
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
    private static readonly HashSet<string> Notices = ["access_violation", "token_expiring", "proposal_pending", "backup_failed", "job_failed", "sync_failed", "banking.consent_expiring", "banking.consent_expired", "banking.sync_failing"];

    private static string NoticeBody(NotificationRecord n) => n.Kind switch
    {
        "access_violation" => n.Params.TryGetValue("request", out var request) && request.Length > 0 ? $"Requête : {request}" : "",
        "token_expiring" => KeyDate(n.Key, "yyyy-MM-dd") is { } on ? $"Expire le {Day(on)}" : "",
        "proposal_pending" => "Une décision est attendue",
        "backup_failed" => BackupReason(n.Params),
        "job_failed" => n.Params.TryGetValue("job", out var job) && job.Length > 0 ? $"Tâche : {job}" : "",
        "sync_failed" => n.Params.TryGetValue("source", out var source) && source.Length > 0 ? $"Source : {source}" : "",
        "banking.consent_expiring" => n.DueOn is { } on ? $"{Bank(n)} — expire le {Day(on)}" : Bank(n),
        "banking.consent_expired" => n.DueOn is { } on ? $"{Bank(n)} — expiré le {Day(on)}" : Bank(n),
        "banking.sync_failing" => Bank(n),
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
        "banking.consent_expiring" or "banking.consent_expired" or "banking.sync_failing" => "/settings/banks",
        "proposal_pending" => $"/proposals/{n.Key[(n.Key.LastIndexOf(':') + 1)..]}",
        "backup_failed" => "/admin/system",
        "job_failed" => "/admin/jobs",
        "sync_failed" => n.Params.GetValueOrDefault("source") is "fx" or "catalog" or "prices" or "irl" ? $"/admin/{n.Params["source"]}" : "/admin/system",
        _ => "/",
    };

    /// <summary>What a row about no item says: a currency code is not an amount, and there is no name to give.</summary>
    private static string HouseholdBody(NotificationRecord n) => n.Kind switch
    {
        "fx_rate_missing" when n.Params.TryGetValue("currency", out var currency) && currency.Length > 0 => $"Devise {currency}",
        "routine_due" => "Des valeurs sont à mettre à jour",
        _ => "",
    };

    private static string Suffix(NotificationRecord n) => n.Kind switch
    {
        "rent_arrears" when KeyDate(n.Key, "yyyy-MM") is { } month => $" — depuis {month.ToString("MMMM yyyy", French)}",
        "valuation_stale" when (n.DueOn ?? KeyDate(n.Key, "yyyy-MM-dd")) is { } on => $" — estimée le {Day(on)}",
        "stale_value" when (n.DueOn ?? KeyDate(n.Key, "yyyy-MM-dd")) is { } on => $" — valeur du {Day(on)}",
        "uc_share_below_floor" when (n.DueOn ?? KeyDate(n.Key, "yyyy-MM-dd")) is { } on => $" — relevé du {Day(on)}",
        "subscriptions.ends_without_renewal" or "revision_due" or "revision_lost" or "irl_missing"
            when (n.DueOn ?? KeyDate(n.Key, "yyyy-MM-dd")) is { } on => $" — le {Day(on)}",
        _ when n.DueOn is { } on => $" — le {Day(on)}",
        _ => "",
    };

    /// <summary>« 1er décembre 2027 », « 31 mars 2027 ».</summary>
    internal static string Day(LocalDate date)
        => (date.Day == 1 ? "1er" : date.Day.ToString(French)) + " " + date.ToString("MMMM yyyy", French);

    /// <summary>The date a key ends with (<see cref="SignalKeys"/>): the month of arrears, the day of a valuation.</summary>
    private static LocalDate? KeyDate(string key, string format)
    {
        var last = key[(key.LastIndexOf(':') + 1)..];
        return LocalDatePattern.CreateWithInvariantCulture(format).Parse(last) is { Success: true } parsed ? parsed.Value : null;
    }

    private static string? LinkOf(NotificationRecord n, NotificationKind kind)
    {
        if (n.ItemIds.Count == 0)
            return n.Kind switch { "fx_rate_missing" => "/dashboard", "routine_due" => "/routine", _ => "/" };
        var id = n.ItemIds[0].Value.ToString("D");
        return kind.Module switch
        {
            "subscriptions" => $"/subscriptions/{id}",
            "loans" => $"/loans/{id}",
            "properties" => PropertyTabs.TryGetValue(n.Kind, out var tab) ? $"/properties/{id}?tab={tab}" : $"/properties/{id}",
            "investments" => InvestmentTabs.TryGetValue(n.Kind, out var tab) ? $"/investments/{id}?tab={tab}" : $"/investments/{id}",
            "income" => $"/income/{id}",
            _ => "/",
        };
    }
}
