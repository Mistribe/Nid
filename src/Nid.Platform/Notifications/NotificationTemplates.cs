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

    public static RenderedNotification Render(NotificationRecord n, NotificationKind kind, IReadOnlyDictionary<ItemId, string> names)
    {
        var title = Titles.GetValueOrDefault(n.Kind, kind.Kind);
        var name = n.ItemIds.Count > 0 && names.TryGetValue(n.ItemIds[0], out var known) && !string.IsNullOrWhiteSpace(known) ? known : UnknownName;
        return new RenderedNotification(title, name + Suffix(n), LinkOf(n, kind));
    }

    private static string Suffix(NotificationRecord n) => n.Kind switch
    {
        "rent_arrears" when KeyDate(n.Key, "yyyy-MM") is { } month => $" — depuis {month.ToString("MMMM yyyy", French)}",
        "valuation_stale" when (n.DueOn ?? KeyDate(n.Key, "yyyy-MM-dd")) is { } on => $" — estimée le {Day(on)}",
        "subscriptions.ends_without_renewal" or "revision_due" or "revision_lost" or "irl_missing"
            when (n.DueOn ?? KeyDate(n.Key, "yyyy-MM-dd")) is { } on => $" — le {Day(on)}",
        _ when n.DueOn is { } on => $" — le {Day(on)}",
        _ => "",
    };

    /// <summary>« 1er décembre 2027 », « 31 mars 2027 ».</summary>
    private static string Day(LocalDate date)
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
            return "/";
        var id = n.ItemIds[0].Value.ToString("D");
        return kind.Module switch
        {
            "subscriptions" => $"/subscriptions/{id}",
            "loans" => $"/loans/{id}",
            "properties" => PropertyTabs.TryGetValue(n.Kind, out var tab) ? $"/properties/{id}?tab={tab}" : $"/properties/{id}",
            _ => "/",
        };
    }
}
