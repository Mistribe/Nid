using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Nid.Core.Application.Dashboard;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Application.Calendar;

/// <summary>
/// Issue #80: « À venir » as an iCalendar feed (RFC 5545): one all-day event per upcoming event, its summary in the dashboard's
/// French, its amount only when the feed shows them. The <c>UID</c> hashes the kind, the item (or the itemless event's label and name)
/// and the date, so a refresh replaces an event rather than duplicating it, and a moved one leaves its old date.
/// </summary>
public static class CalendarIcs
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    public static string Write(IReadOnlyList<DashboardEventView> events, bool amounts, string publicUrl, Instant stamp)
    {
        var b = new StringBuilder();
        void Line(string line) => Fold(b, line);
        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:-//Nid//Calendrier//FR");
        Line("CALSCALE:GREGORIAN");
        Line("METHOD:PUBLISH");
        Line("X-WR-CALNAME:Nid");
        Line("REFRESH-INTERVAL;VALUE=DURATION:PT6H");
        Line("X-PUBLISHED-TTL:PT6H");
        var dtstamp = stamp.InUtc().LocalDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        // Two itemless events with the same key (two declarants of one name): a UID is unique in the feed, numbered in the server's order.
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            var summary = Summary(e);
            if (summary is null) continue; // a kind this feed does not know (a newer module): left out rather than shown raw
            if (amounts && e.Amount is { } money) summary += $" · {Amount(money)}";
            var url = $"{publicUrl.TrimEnd('/')}{PathOf(e)}";
            var uid = Uid(e);
            var repeat = seen[uid] = seen.GetValueOrDefault(uid) + 1;
            Line("BEGIN:VEVENT");
            Line($"UID:{uid}{(repeat > 1 ? $"-{repeat}" : "")}@nid");
            Line($"DTSTAMP:{dtstamp}");
            Line($"DTSTART;VALUE=DATE:{Date(e.On)}");
            Line($"DTEND;VALUE=DATE:{Date(e.On.PlusDays(1))}");
            Line($"SUMMARY:{Escape(summary)}");
            Line($"DESCRIPTION:{Escape(url)}");
            Line($"URL:{url}");
            Line("TRANSP:TRANSPARENT");
            Line("END:VEVENT");
        }
        Line("END:VCALENDAR");
        return b.ToString();
    }

    /// <summary>The dashboard's line: what happens, then what it is about (« Renouvellement · Netflix »).</summary>
    public static string? Summary(DashboardEventView e)
    {
        var what = What(e);
        if (what is null) return null;
        var about = e.Id is not null ? e.Name
            : e.Label == "household" ? "Foyer (plafonds mutualisés)"
            : e.Label == "department_unknown" ? "Date la plus tôt : renseignez le département dans le profil fiscal"
            : e.Name;
        return string.IsNullOrWhiteSpace(about) ? what : $"{what} · {about}";
    }

    // web/src/locales/fr.json, dashboard.upcoming: the same words as « À venir ».
    private static string? What(DashboardEventView e) => e.Kind switch
    {
        UpcomingKinds.SubscriptionRenewal => "Renouvellement",
        UpcomingKinds.SubscriptionExpiry => "Expire",
        UpcomingKinds.SubscriptionCancelDeadline => "Dernier jour pour résilier",
        UpcomingKinds.IncomePhaseEnd => $"Fin de « {e.Label} »",
        UpcomingKinds.LoanPhaseEnd => $"Fin de la phase {e.Label} : taux ou différé qui change",
        UpcomingKinds.LoanEnd => "Dernière échéance",
        UpcomingKinds.ValuationDue => "Estimation à refaire",
        UpcomingKinds.PropertyCostDue => Category(e.Label),
        UpcomingKinds.TenancyEnd => $"Fin du bail « {e.Label} »",
        UpcomingKinds.HoldingMaturity => $"Échéance de « {e.Label} » : vérifier où ira le capital",
        UpcomingKinds.TaxMilestone => e.Label switch
        {
            "life_insurance_8y" => "8 ans du contrat : la fiscalité des retraits change",
            "pea_5y" => "5 ans du PEA : la fiscalité des retraits change",
            "per_retirement" => "Âge de la retraite : le PER peut être débloqué",
            _ => null,
        },
        UpcomingKinds.PerDeadline => "PER : reste à verser avant le 31 décembre pour utiliser le plafond",
        UpcomingKinds.PerCeilingExpiring => "PER : minimum à verser avant le 31 décembre pour ne rien perdre",
        UpcomingKinds.TaxFilingDeadline => "Impôt : date limite de la déclaration en ligne",
        UpcomingKinds.TaxNewRate => "Impôt : le nouveau taux de prélèvement s’applique",
        UpcomingKinds.TaxBalanceDue => "Impôt : prélèvement du solde",
        UpcomingKinds.PropertyTaxDue => "Taxe foncière à payer",
        _ => null,
    };

    // web/src/locales/fr.json, property_flows.category_option.
    private static string Category(string? code) => code switch
    {
        "works" => "Travaux",
        "property_tax" => "Taxe foncière",
        "condo_fees" => "Charges de copropriété",
        "insurance" => "Assurance",
        "energy" => "Énergie",
        "maintenance" => "Entretien",
        "management_fees" => "Frais de gestion",
        "other" => "Autre",
        _ => code ?? "",
    };

    /// <summary>The item's page, as the web's <c>itemRoute</c>; the PER deadline and the tax calendar lead to the tax page.</summary>
    public static string PathOf(DashboardEventView e) => e.Id is not { } id ? "/tax" : e.Module switch
    {
        "subscriptions" => $"/subscriptions/{id}",
        "income" => $"/income/{id}",
        "properties" => $"/properties/{id}",
        "loans" => $"/loans/{id}",
        _ => $"/investments/{id}",
    };

    public static string Uid(DashboardEventView e)
    {
        var key = string.Join('\u001f', e.Kind, e.Id?.ToString() ?? $"{e.Module}:{e.Label}:{e.Name}", e.Label ?? "", Date(e.On));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
    }

    /// <summary>« 1 234,50 € »: the server's figure, only formatted.</summary>
    private static string Amount(DashboardMoneyView money)
    {
        var value = decimal.Parse(money.Amount, CultureInfo.InvariantCulture);
        var text = value.ToString("#,##0.00", French);
        return money.Currency == "EUR" ? $"{text} €" : $"{text} {money.Currency}";
    }

    private static string Date(LocalDate on) => on.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>RFC 5545 §3.3.11: a backslash, a semicolon, a comma and a newline are escaped in a TEXT value.</summary>
    public static string Escape(string text)
        => text.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");

    /// <summary>RFC 5545 §3.1: lines of 75 octets at most, continued after CRLF and a space, never inside a UTF-8 character.</summary>
    private static void Fold(StringBuilder b, string line)
    {
        var limit = 75;
        var octets = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (octets + size > limit)
            {
                b.Append("\r\n ");
                octets = 1;
                limit = 75;
            }
            b.Append(rune.ToString());
            octets += size;
        }
        b.Append("\r\n");
    }
}
