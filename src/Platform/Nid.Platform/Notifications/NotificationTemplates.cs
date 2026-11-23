using System.Globalization;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;

namespace Nid.Platform.Notifications;

/// <summary>
/// A notification as it is worded. No amount, except the figures a signal was raised on when the centre asks for them (issue #81):
/// outside the application the screen it links to has the figures.
/// </summary>
public sealed record RenderedNotification(string Title, string Body, string? Link);

/// <summary>
/// Renders a notification from its kind's <see cref="NotificationTemplate"/>, which its module declares (issue #514). A kind without one
/// reads as its name, with no body and the home page.
/// </summary>
public static class NotificationTemplates
{
    private const string UnknownName = "Élément";

    /// <summary>Issue #81: the figures a signal carries (params ending in <c>_amount</c>), named, in this order.</summary>
    private static readonly (string Param, string Name)[] Figures = [
        ("net_worth_amount", "Patrimoine"), ("step_amount", "Palier"),
        ("recent_amount", "Épargne par mois sur 3 mois"), ("average_amount", "sur 12 mois"),
        ("balance_amount", "Solde"), ("charges_amount", "Charges à venir"),
        ("above_amount", "Au-dessus de l'épargne de précaution"), ("buffer_amount", "Épargne de précaution"), ("room_amount", "Place sur les livrets"),
        ("spent_amount", "Dépensé ce mois"), ("usual_amount", "Mois habituel"), ("budget_amount", "Budget"), ("projected_amount", "Fin de mois au rythme actuel"),
    ];

    /// <summary>The title of a kind, as the preferences screen lists it; the kind itself when it has none.</summary>
    public static string TitleOf(NotificationKind kind) => kind.Template?.Title ?? kind.Kind;

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
                shown.Add($"{name} : {Math.Round(amount, 0, MidpointRounding.AwayFromZero).ToString("N0", NotificationFacts.French)}\u00a0{currency}");
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
        var template = kind.Template ?? new NotificationTemplate(kind.Kind);
        var facts = new NotificationFacts(n.Kind, n.Key, n.ItemIds.Count > 0 ? n.ItemIds[0] : null, n.DueOn, n.Params);
        var link = template.Link?.Invoke(facts) ?? "/";
        // A notice never names an item: it says what happened, not about what.
        if (template.Notice || facts.Item is not { } item)
            return new RenderedNotification(template.Title, template.Body?.Invoke(facts) ?? "", link);

        var name = names.TryGetValue(item, out var known) && !string.IsNullOrWhiteSpace(known) ? known : UnknownName;
        return new RenderedNotification(template.Title, name + (template.Suffix?.Invoke(facts) ?? facts.OnDueDate), link);
    }
}
