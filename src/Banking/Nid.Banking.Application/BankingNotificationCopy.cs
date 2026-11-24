using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;

namespace Nid.Banking.Application;

/// <summary>Issue #514: the French copy of the banking kinds (notifications design §4), declared with them by <see cref="BankingNotificationSource"/>.</summary>
internal static class BankingNotificationCopy
{
    private const string Banks = "/settings/banks";

    public static readonly NotificationTemplate ConsentExpiring = new("Le consentement d'une banque expire bientôt")
    {
        Notice = true, Body = f => f.DueOn is { } on ? $"{Bank(f)} — expire le {NotificationFacts.Day(on)}" : Bank(f), Link = _ => Banks,
    };

    public static readonly NotificationTemplate ConsentExpired = new("Le consentement d'une banque a expiré")
    {
        Notice = true, Body = f => f.DueOn is { } on ? $"{Bank(f)} — expiré le {NotificationFacts.Day(on)}" : Bank(f), Link = _ => Banks,
    };

    public static readonly NotificationTemplate SyncFailing = new("La synchronisation d'une banque échoue") { Notice = true, Body = Bank, Link = _ => Banks };

    public static readonly NotificationTemplate ApplicationNeeded = new("Reliez à nouveau votre banque")
    {
        Notice = true,
        Body = _ => "La configuration bancaire du foyer a changé : créez votre propre application Enable Banking puis reliez à nouveau votre banque.",
        Link = _ => $"{Banks}?tab=application",
    };

    public static readonly NotificationTemplate AmountDiffers = new("Montant différent du prévu") { Link = f => OnItem(f, "/") };

    public static readonly NotificationTemplate ChargeMissing = new("Charge attendue non prélevée") { Link = f => OnItem(f, "/") };

    public static readonly NotificationTemplate ShortfallAhead = new("Un compte courant risque de manquer d'argent")
    {
        Body = f => f.DueOn is not { } on ? ""
            : f.Params.TryGetValue("account", out var account) ? $"Le {NotificationFacts.Day(on)} — {account}" : $"Le {NotificationFacts.Day(on)}",
        Link = f => OnItem(f, Banks),
    };

    public static readonly NotificationTemplate IdleCash = new("Vos comptes courants dépassent l'épargne de précaution")
    {
        Body = f => "Au-dessus de l'épargne de précaution depuis 4 semaines" + f.Params.GetValueOrDefault("reason") switch
        {
            "room" => ", un livret a de la place",
            "goal_behind" => ", un objectif est en retard",
            "room_goal_behind" => ", un livret a de la place et un objectif est en retard",
            _ => "",
        },
        Link = f => OnItem(f, "/investments"),
    };

    public static readonly NotificationTemplate CardUnused = new("Une carte n'a pas assez servi ce mois-ci")
    {
        Body = f => f.DueOn is { } on ? CardUnusedText(f.Params, on) : "",
        Link = f => OnItem(f, Banks),
    };

    public static readonly NotificationTemplate UnusualSpending = new("Des dépenses sortent de l'ordinaire")
    {
        Body = f => $"{(f.Params.GetValueOrDefault("name") is { Length: > 0 } name ? name : "Une catégorie")} : " + f.Params.GetValueOrDefault("reason") switch
        {
            "budget_over" => "budget dépassé",
            "budget_pace" => "le budget sera dépassé au rythme actuel",
            _ => "bien au-dessus du mois habituel",
        },
        Link = f => OnItem(f, SpendingLink(f.Params)),
    };

    private static string Bank(NotificationFacts f) => f.Params.GetValueOrDefault("bank") ?? "";

    /// <summary>
    /// A finding about an item opens the item's page, which <c>Params["module"]</c> names (the item being any module's); one about none opens
    /// <paramref name="household"/>.
    /// </summary>
    private static string OnItem(NotificationFacts f, string household) => f.Item is null ? household : f.Params.GetValueOrDefault("module") switch
    {
        "subscriptions" => f.ItemPage("subscriptions"),
        "loans" => f.ItemPage("loans"),
        "properties" => f.ItemPage("properties"),
        "income" => f.ItemPage("income"),
        "investments" => f.ItemPage("investments", f.Kind == BankingNotificationSource.ShortfallAhead ? "operations" : null),
        "childcare" => f.Params.TryGetValue("month", out var month) ? $"{f.ItemPage("childcare")}/statements/{month}" : f.ItemPage("childcare"),
        _ => "/",
    };

    /// <summary>#362: « Carte Visa •••• 1234 : aucun paiement ce mois-ci, 1 attendu. Utilisez-la avant le 30 novembre. » The masked number only.</summary>
    private static string CardUnusedText(IReadOnlyDictionary<string, string> p, LocalDate by)
    {
        var name = string.Join(" ", new[] { p.GetValueOrDefault("card")?.Trim(), p.GetValueOrDefault("number") is { Length: > 0 } last4 ? $"•••• {last4}" : null }
            .Where(s => !string.IsNullOrEmpty(s)));
        var count = p.GetValueOrDefault("count") is "0" or null ? "aucun paiement" : $"{p["count"]} paiement{(p["count"] == "1" ? "" : "s")}";
        var min = p.GetValueOrDefault("min") ?? "1";
        return $"{(name.Length > 0 ? name : "Une carte")} : {count} ce mois-ci, {min} attendu{(min == "1" ? "" : "s")}. Utilisez-la avant le {NotificationFacts.Day(by)}.";
    }

    /// <summary>Issue #305: « Opérations » on the lines the category's figure counts that month; the spending page without them.</summary>
    private static string SpendingLink(IReadOnlyDictionary<string, string> p)
        => Guid.TryParse(p.GetValueOrDefault("category"), out var category) && YearMonthPattern.Iso.Parse(p.GetValueOrDefault("month") ?? "") is { Success: true } month
            ? $"/spending?tab=operations&category={category:D}&from={month.Value.ToDateInterval().Start:yyyy-MM-dd}&to={month.Value.ToDateInterval().End:yyyy-MM-dd}&state=counted"
            : "/spending";
}
