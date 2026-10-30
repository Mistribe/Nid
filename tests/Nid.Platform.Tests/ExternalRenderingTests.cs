using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Platform.Notifications;
using NodaTime;

namespace Nid.Platform.Tests;

/// <summary>Notifications design §5.3: the wording for the outside carries no amount and an absolute link.</summary>
public class ExternalRenderingTests
{
    private const string Base = "https://nid.example/";

    private static readonly ItemId Item = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private static readonly (string Kind, string Module)[] Kinds =
    [
        ("subscription_renewal", "subscriptions"), ("subscription_expiry", "subscriptions"),
        ("subscription_cancel_deadline", "subscriptions"),
        ("subscriptions.ends_without_renewal", "subscriptions"), ("tenancy_end", "properties"), ("property_cost_due", "properties"),
        ("rent_arrears", "properties"), ("revision_due", "properties"), ("revision_lost", "properties"), ("irl_missing", "properties"),
        ("valuation_stale", "properties"), ("loan_phase_end", "loans"), ("loan_end", "loans"),
    ];

    private readonly ExternalNotificationRenderer renderer = new(new NotificationLinkBase(Base));

    private static NotificationRecord Record(
        string kind, NotificationLevel level = NotificationLevel.Action, LocalDate? dueOn = null, IReadOnlyList<ItemId>? items = null,
        IReadOnlyDictionary<string, string>? parameters = null)
        => new(Guid.NewGuid(), new PersonId(Guid.NewGuid()), kind, "k", level, items ?? [Item], dueOn, new LocalDate(2027, 3, 1),
            parameters ?? new Dictionary<string, string>(), NotificationStates.Open, null, null, Instant.FromUtc(2027, 3, 1, 0, 0), null, null);

    private static NotificationKind KindOf(string kind, string module, NotificationLevel level = NotificationLevel.Action)
        => new(kind, "test", level, module, null);

    private static IReadOnlyDictionary<ItemId, string> Names(string name) => new Dictionary<ItemId, string> { [Item] = name };

    [Fact]
    public void Subject_never_carries_the_name()
    {
        var message = renderer.One(
            Record("tenancy_end", dueOn: new LocalDate(2027, 3, 31)), KindOf("tenancy_end", "properties"), Names("Maison"));

        Assert.Equal("Nid : Un bail arrive à échéance", message.Subject);
        Assert.Contains("Maison", message.Text);
        Assert.Contains("le 31 mars 2027", message.Text);
        Assert.Contains($"https://nid.example/properties/{Item.Value:D}?tab=rental", message.Text);
        Assert.DoesNotContain("Maison", message.Subject);
        Assert.Contains("<p><strong>Un bail arrive à échéance</strong></p>", message.Html);
        Assert.Contains($"<a href=\"https://nid.example/properties/{Item.Value:D}?tab=rental\">Ouvrir dans Nid</a>", message.Html);
    }

    [Fact]
    public void Household_has_no_name()
    {
        var message = renderer.Household(Record("tenancy_end", dueOn: new LocalDate(2027, 3, 31)), KindOf("tenancy_end", "properties"));

        Assert.Equal("Nid : Un bail arrive à échéance", message.Subject);
        Assert.Contains("Un bail arrive à échéance", message.Text);
        Assert.Contains("Échéance : le 31 mars 2027", message.Text);
        Assert.Contains($"Ouvrir dans Nid : https://nid.example/properties/{Item.Value:D}?tab=rental", message.Text);
        Assert.DoesNotContain("Appartement", message.Text);
        Assert.Contains("le 31 mars 2027", message.Html);
    }

    [Fact]
    public void The_html_encodes_the_name()
    {
        var message = renderer.One(Record("loan_end", dueOn: new LocalDate(2027, 3, 31)), KindOf("loan_end", "loans"), Names("<b>Prêt & co</b>"));

        Assert.DoesNotContain("<b>", message.Html);
        Assert.Contains("&lt;b&gt;Prêt &amp; co&lt;/b&gt;", message.Html);
    }

    [Fact]
    public void An_item_less_row_has_no_name_line_and_links_to_its_screen()
    {
        var message = renderer.One(Record("routine_due", items: []), KindOf("routine_due", "core"), Names("Maison"));

        Assert.DoesNotContain("Élément", message.Text);
        Assert.DoesNotContain("Maison", message.Text);
        Assert.Contains("https://nid.example/routine", message.Text);
        Assert.DoesNotContain("https://nid.example//", message.Text);
    }

    [Fact]
    public void An_item_less_row_says_its_body_and_a_currency_code_survives_the_amount_scrub()
    {
        var rate = renderer.One(Record("fx_rate_missing", items: [], parameters: new Dictionary<string, string> { ["currency"] = "USD" }),
            KindOf("fx_rate_missing", "core"), Names("Maison"));
        var routine = renderer.One(Record("routine_due", items: []), KindOf("routine_due", "core"), Names("Maison"));

        Assert.Equal("Nid : Un taux de change manque", rate.Subject);
        Assert.Contains("Devise USD", rate.Text);
        Assert.Contains("<p>Devise USD</p>", rate.Html);
        Assert.Contains("https://nid.example/dashboard", rate.Text);
        Assert.Contains("Des valeurs sont à mettre à jour", routine.Text);
        Assert.Contains("https://nid.example/routine", routine.Text);
    }

    [Fact]
    public void No_amount_in_any_rendering()
    {
        var parameters = new Dictionary<string, string> { ["amount"] = "1200,00 €", ["rent"] = "1 200,00 €", ["total"] = "$1,200.00" };
        foreach (var (kind, module) in Kinds)
        foreach (var name in new[] { "Loyer 1200,00 €", "Maison — 1 200,00 € / mois", "Prêt $1,200.00", "EUR 1200", "1200 EUR" })
        {
            var row = Record(kind, dueOn: new LocalDate(2027, 3, 31), parameters: parameters);
            var one = renderer.One(row, KindOf(kind, module), Names(name));
            var digest = renderer.Digest([(row, KindOf(kind, module))], Names(name));

            foreach (var text in new[] { one.Subject, one.Text, one.Html, digest.Subject, digest.Text, digest.Html })
            {
                Assert.DoesNotContain("1200", text);
                Assert.DoesNotContain("1 200", text);
                Assert.DoesNotContain("1,200", text);
                Assert.DoesNotContain("€", text);
                Assert.DoesNotContain("$", text);
            }

            Assert.Contains(Item.Value.ToString("D"), one.Text);
        }
    }

    [Fact]
    public void The_scrub_trims_a_year_before_a_currency_and_keeps_plain_names()
    {
        string TextFor(string name) => renderer.One(Record("loan_end"), KindOf("loan_end", "loans"), Names(name)).Text;

        // Known and accepted: a year followed by a currency code reads as an amount, so the name is cut there.
        var trimmed = TextFor("Fonds 2024 EUR");
        Assert.Contains("Fonds", trimmed);
        Assert.DoesNotContain("2024", trimmed);
        Assert.DoesNotContain("EUR", trimmed);

        foreach (var name in new[] { "Appart 75 m²", "Livret A 2024", "PEA 2" })
            Assert.Contains(name, TextFor(name));
    }

    [Fact]
    public void A_name_without_amount_is_kept_as_it_is()
    {
        var message = renderer.One(Record("loan_end"), KindOf("loan_end", "loans"), Names("Crédit 2024 — Société Générale"));

        Assert.Contains("Crédit 2024 — Société Générale", message.Text);
    }

    [Fact]
    public void Digest_groups_by_level_and_counts()
    {
        var other = new ItemId(Guid.Parse("33333333-3333-3333-3333-333333333333"));
        var rows = new List<(NotificationRecord, NotificationKind)>
        {
            (Record("valuation_stale", NotificationLevel.Info, items: [other]), KindOf("valuation_stale", "properties", NotificationLevel.Info)),
            (Record("rent_arrears", NotificationLevel.Critical), KindOf("rent_arrears", "properties", NotificationLevel.Critical)),
            (Record("tenancy_end", NotificationLevel.Info, new LocalDate(2027, 3, 31)), KindOf("tenancy_end", "properties", NotificationLevel.Info)),
        };
        var names = new Dictionary<ItemId, string> { [Item] = "Maison", [other] = "Studio" };

        var digest = renderer.Digest(rows, names);

        Assert.Equal("Nid : 3 notifications", digest.Subject);
        var important = digest.Text.IndexOf("Important", StringComparison.Ordinal);
        var information = digest.Text.IndexOf("Pour information", StringComparison.Ordinal);
        Assert.True(important >= 0 && information > important, digest.Text);
        Assert.DoesNotContain("À traiter", digest.Text);
        Assert.Contains("Des loyers sont impayés", digest.Text);
        Assert.Contains($"https://nid.example/properties/{Item.Value:D}?tab=rental", digest.Text);
        Assert.Contains("<h2>Important</h2>", digest.Html);
        Assert.True(digest.Html.IndexOf("Important", StringComparison.Ordinal) < digest.Html.IndexOf("Pour information", StringComparison.Ordinal));
    }

    [Fact]
    public void Digest_puts_actions_between_the_critical_and_the_info_rows()
    {
        var rows = new List<(NotificationRecord, NotificationKind)>
        {
            (Record("loan_end", NotificationLevel.Info), KindOf("loan_end", "loans", NotificationLevel.Info)),
            (Record("loan_phase_end", NotificationLevel.Action), KindOf("loan_phase_end", "loans")),
        };

        var text = renderer.Digest(rows, Names("Prêt")).Text;

        Assert.True(text.IndexOf("À traiter", StringComparison.Ordinal) < text.IndexOf("Pour information", StringComparison.Ordinal), text);
    }

    [Fact]
    public void One_row_makes_a_singular_subject()
    {
        var digest = renderer.Digest([(Record("loan_end"), KindOf("loan_end", "loans"))], Names("Prêt"));

        Assert.Equal("Nid : 1 notification", digest.Subject);
    }
}
