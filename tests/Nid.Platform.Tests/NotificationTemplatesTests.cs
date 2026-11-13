using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Platform.Notifications;
using NodaTime;

namespace Nid.Platform.Tests;

/// <summary>Notifications design §4: a notification names the item and the date, never an amount.</summary>
public class NotificationTemplatesTests
{
    private static readonly ItemId Item = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private static readonly (string Kind, string Module)[] Kinds =
    [
        ("subscription_renewal", "subscriptions"), ("subscription_expiry", "subscriptions"),
        ("subscription_cancel_deadline", "subscriptions"),
        ("subscriptions.ends_without_renewal", "subscriptions"),
        ("tenancy_end", "properties"), ("property_cost_due", "properties"), ("rent_arrears", "properties"),
        ("revision_due", "properties"), ("revision_lost", "properties"), ("irl_missing", "properties"),
        ("valuation_stale", "properties"), ("property_tax_due", "properties"),
        ("loan_phase_end", "loans"), ("loan_end", "loans"),
        ("holding_maturity", "investments"), ("stale_value", "investments"), ("uc_share_below_floor", "investments"),
        ("income_phase_end", "income"), ("income_gap_soon", "income"),
        ("childcare_statement_due", "childcare"), ("childcare_below_minimum", "childcare"),
    ];

    private static NotificationRecord Record(string kind, LocalDate? dueOn = null, string key = "k")
        => new(Guid.NewGuid(), new PersonId(Guid.NewGuid()), kind, key, NotificationLevel.Info, [Item], dueOn, new LocalDate(2027, 3, 1),
            new Dictionary<string, string>(), NotificationStates.Open, null, null, Instant.FromUtc(2027, 3, 1, 0, 0), null, null);

    private static NotificationKind KindOf(string kind, string module) => new(kind, "test", NotificationLevel.Info, module, null);

    [Fact]
    public void Every_kind_has_a_title_and_a_link()
    {
        foreach (var (kind, module) in Kinds)
        {
            var rendered = NotificationTemplates.Render(Record(kind), KindOf(kind, module), new Dictionary<ItemId, string> { [Item] = "Maison" });
            Assert.False(string.IsNullOrWhiteSpace(rendered.Title), kind);
            Assert.StartsWith("/", rendered.Link);
            Assert.Contains(Item.Value.ToString("D"), rendered.Link);
            Assert.DoesNotContain("Maison", rendered.Title);
        }
    }

    [Fact]
    public void Body_has_the_name_and_the_date_never_an_amount()
    {
        var rendered = NotificationTemplates.Render(Record("tenancy_end", new LocalDate(2027, 3, 31)), KindOf("tenancy_end", "properties"),
            new Dictionary<ItemId, string> { [Item] = "Maison" });

        Assert.Equal("Maison — le 31 mars 2027", rendered.Body);
        Assert.Equal("Un bail arrive à échéance", rendered.Title);
        Assert.Equal($"/properties/{Item.Value:D}?tab=rental", rendered.Link);
    }

    [Fact]
    public void Findings_name_their_month_or_date_and_a_missing_name_is_an_element()
    {
        var none = new Dictionary<ItemId, string>();
        var arrears = NotificationTemplates.Render(Record("rent_arrears", key: $"rent_arrears:{Item.Value:D}:2027-02"), KindOf("rent_arrears", "properties"),
            new Dictionary<ItemId, string> { [Item] = "Studio" });
        var stale = NotificationTemplates.Render(Record("valuation_stale", key: $"valuation_stale:{Item.Value:D}:2025-06-15"), KindOf("valuation_stale", "properties"), none);
        var loan = NotificationTemplates.Render(Record("loan_end", new LocalDate(2027, 12, 1)), KindOf("loan_end", "loans"), none);

        Assert.Equal("Studio — depuis février 2027", arrears.Body);
        var statement = NotificationTemplates.Render(Record("childcare_statement_due", new LocalDate(2025, 9, 30), $"childcare_statement_due:{Item.Value:D}:2025-09"),
            KindOf("childcare_statement_due", "childcare"), new Dictionary<ItemId, string> { [Item] = "Garde de Léa" });
        Assert.Equal("Garde de Léa — relevé de septembre 2025", statement.Body);
        Assert.Equal("Élément — estimée le 15 juin 2025", stale.Body);
        Assert.Equal($"/properties/{Item.Value:D}?tab=valuations", stale.Link);
        Assert.Equal("Élément — le 1er décembre 2027", loan.Body);
        Assert.Equal($"/loans/{Item.Value:D}", loan.Link);
    }

    [Fact]
    public void Household_rows_have_no_name_line()
    {
        NotificationRecord Household(string kind, string key, Dictionary<string, string> parameters)
            => Record(kind, key: key) with { ItemIds = [], Params = parameters };
        var rate = NotificationTemplates.Render(Household("fx_rate_missing", "fx_rate_missing:USD", new() { ["currency"] = "USD" }), KindOf("fx_rate_missing", "core"),
            new Dictionary<ItemId, string> { [Item] = "Maison" });
        var routine = NotificationTemplates.Render(Household("routine_due", "routine_due:2027-03", []), KindOf("routine_due", "core"), new Dictionary<ItemId, string>());

        Assert.Equal(("Un taux de change manque", "Devise USD", "/dashboard"), (rate.Title, rate.Body, rate.Link));
        Assert.Equal(("La routine du mois est à faire", "Des valeurs sont à mettre à jour", "/routine"), (routine.Title, routine.Body, routine.Link));
    }

    [Fact]
    public void The_month_changes_name_directions_and_causes_never_an_amount()
    {
        var record = Record("month_changes", key: "month_changes:2027-03") with
        {
            ItemIds = [],
            Params = new Dictionary<string, string> { ["income"] = "down", ["net_worth"] = "up|investments.value+|real_estate.paid_in+", ["commitments"] = "up|subscriptions+|unknown-" },
        };

        var rendered = NotificationTemplates.Render(record, KindOf("month_changes", "core"), new Dictionary<ItemId, string>());

        Assert.Equal(("Ce qui a changé depuis le mois dernier",
            "Patrimoine en hausse — valeur des placements ↑, capital remboursé ↑ · Engagements en hausse — abonnements ↑, autres ↓ · Revenus en baisse",
            "/dashboard"), (rendered.Title, rendered.Body, rendered.Link));
    }

    [Fact]
    public void The_tax_calendar_opens_the_tax_page_and_names_the_day()
    {
        RenderedNotification Tax(string kind, Dictionary<string, string> parameters) => NotificationTemplates.Render(
            Record(kind, new LocalDate(2026, 5, 21), $"{kind}:2026-05-21") with { ItemIds = [], Params = parameters }, KindOf(kind, "core"), new Dictionary<ItemId, string>());

        Assert.Equal(("La déclaration de revenus est à faire", "En ligne avant le 21 mai 2026", "/tax"),
            (Tax("tax_filing_deadline", []).Title, Tax("tax_filing_deadline", []).Body, Tax("tax_filing_deadline", []).Link));
        Assert.Equal("En ligne avant le 21 mai 2026, la date la plus tôt : département non renseigné",
            Tax("tax_filing_deadline", new() { ["note"] = "department_unknown" }).Body);
        Assert.Equal(("À partir du 21 mai 2026", "/tax"), (Tax("tax_balance_due", []).Body, Tax("tax_new_rate", []).Link));
    }

    [Fact]
    public void A_failed_job_opens_its_own_page()
    {
        NotificationRecord Job(Dictionary<string, string> parameters) => Record("job_failed", key: "job_failed:fx-daily") with { ItemIds = [], Params = parameters };

        Assert.Equal("/admin/jobs/fx-daily", NotificationTemplates.Render(Job(new() { ["job"] = "fx-daily" }), KindOf("job_failed", "platform"), new Dictionary<ItemId, string>()).Link);
        Assert.Equal("/admin/jobs", NotificationTemplates.Render(Job([]), KindOf("job_failed", "platform"), new Dictionary<ItemId, string>()).Link);
    }

    [Fact]
    public void Investments_and_income_open_their_screens_and_name_the_date()
    {
        var names = new Dictionary<ItemId, string> { [Item] = "Assurance-vie" };
        var maturity = NotificationTemplates.Render(Record("holding_maturity", new LocalDate(2027, 12, 1)), KindOf("holding_maturity", "investments"), names);
        var stale = NotificationTemplates.Render(Record("stale_value", key: $"stale_value:{Item.Value:D}:2025-06-15"), KindOf("stale_value", "investments"), names);
        var floor = NotificationTemplates.Render(Record("uc_share_below_floor", key: $"uc_share_below_floor:{Item.Value:D}:2024-12-31"), KindOf("uc_share_below_floor", "investments"), names);
        var phase = NotificationTemplates.Render(Record("income_phase_end", new LocalDate(2027, 3, 31)), KindOf("income_phase_end", "income"), names);
        var gap = NotificationTemplates.Render(Record("income_gap_soon", new LocalDate(2027, 3, 31)), KindOf("income_gap_soon", "income"), names);

        Assert.Equal(("Un support arrive à échéance", "Assurance-vie — le 1er décembre 2027", $"/investments/{Item.Value:D}?tab=holdings"),
            (maturity.Title, maturity.Body, maturity.Link));
        Assert.Equal(("Une valeur est à mettre à jour", "Assurance-vie — valeur du 15 juin 2025", $"/investments/{Item.Value:D}"),
            (stale.Title, stale.Body, stale.Link));
        Assert.Equal(("La part en unités de compte est sous le plancher", "Assurance-vie — relevé du 31 décembre 2024", $"/investments/{Item.Value:D}?tab=holdings"),
            (floor.Title, floor.Body, floor.Link));
        Assert.Equal(("Une phase de revenu se termine", "Assurance-vie — le 31 mars 2027", $"/income/{Item.Value:D}"), (phase.Title, phase.Body, phase.Link));
        Assert.Equal(("Un trou de revenus approche", "Assurance-vie — le 31 mars 2027", $"/income/{Item.Value:D}"), (gap.Title, gap.Body, gap.Link));
    }

    [Fact]
    public void A_banking_finding_opens_the_page_of_its_item()
    {
        foreach (var (kind, module, title, link) in new[]
        {
            ("banking.amount_differs", "loans", "Montant différent du prévu", "/loans/"),
            ("banking.charge_missing", "subscriptions", "Charge attendue non prélevée", "/subscriptions/"),
            ("banking.charge_missing", "properties", "Charge attendue non prélevée", "/properties/"),
        })
        {
            var record = Record(kind, new LocalDate(2027, 3, 5)) with { Params = new Dictionary<string, string> { ["module"] = module } };

            var rendered = NotificationTemplates.Render(record, KindOf(kind, "banking"), new Dictionary<ItemId, string> { [Item] = "Maison" });

            Assert.Equal((title, link + Item.Value.ToString("D"), "Maison — le 5 mars 2027"), (rendered.Title, rendered.Link, rendered.Body));
        }
    }

    [Fact]
    public void Published_kinds_have_a_title_a_body_without_name_or_amount_and_their_link()
    {
        NotificationRecord Published(string kind, string key, Dictionary<string, string>? parameters = null, bool withItem = false)
            => Record(kind, key: key) with { ItemIds = withItem ? [Item] : [], Params = parameters ?? [] };
        var names = new Dictionary<ItemId, string> { [Item] = "Maison" };
        var proposal = Guid.NewGuid();

        var cases = new[]
        {
            (Published("access_violation", "access_violation:1", new() { ["request"] = "GetEnvelope", ["admin"] = "1" }), "Une réponse a été bloquée par la garde d'accès", "/admin/security"),
            (Published("access_violation", "access_violation:1", new() { ["request"] = "GetEnvelope" }), "Une réponse a été bloquée par la garde d'accès", "/"),
            (Published("token_expiring", "token_expiring:abc:2027-03-31"), "Un jeton d'accès expire bientôt", "/security"),
            (Published("banking.consent_expiring", "banking.consent_expiring:abc:2027-03-31:14", new() { ["bank"] = "Banque Alpha" }), "Le consentement d'une banque expire bientôt", "/settings/banks"),
            (Published("banking.consent_expired", "banking.consent_expired:abc:2027-03-31"), "Le consentement d'une banque a expiré", "/settings/banks"),
            (Published("banking.sync_failing", "banking.sync_failing:abc:2027-03-01"), "La synchronisation d'une banque échoue", "/settings/banks"),
            (Published("proposal_pending", $"proposal_pending:{proposal}", withItem: true), "Une proposition attend votre décision", $"/proposals/{proposal}"),
            (Published("backup_failed", "backup_failed:never"), "La sauvegarde a échoué", "/admin/system"),
            (Published("job_failed", "job_failed:fx-sync", new() { ["job"] = "fx-sync" }), "Une tâche planifiée a échoué", "/admin/jobs/fx-sync"),
            (Published("sync_failed", "sync_failed:prices:never", new() { ["source"] = "prices" }), "Une synchronisation a échoué", "/admin/prices"),
        };
        foreach (var (record, title, link) in cases)
        {
            var rendered = NotificationTemplates.Render(record, KindOf(record.Kind, "platform"), names);
            Assert.Equal((title, link), (rendered.Title, rendered.Link));
            Assert.DoesNotContain("Maison", rendered.Body);
            Assert.DoesNotMatch(@"\d+\s?(€|EUR)", rendered.Body);
        }

        Assert.Equal("Requête : GetEnvelope", NotificationTemplates.Render(cases[0].Item1, KindOf("access_violation", "platform"), names).Body);
    }

    [Theory]
    [InlineData("database", null, "Étape en échec : database")]
    [InlineData("too_old", "26", "Aucune sauvegarde réussie depuis plus de 26 h")]
    [InlineData("too_old", null, "Aucune sauvegarde réussie depuis trop longtemps")]
    [InlineData("remote", null, "La copie distante a échoué")]
    [InlineData("unreadable", null, "L'état de la sauvegarde est illisible")]
    [InlineData("configuration", null, "La configuration de la sauvegarde est invalide")]
    public void Backup_failed_names_its_reason(string reason, string? hours, string body)
    {
        var parameters = new Dictionary<string, string> { ["reason"] = reason };
        if (hours is not null)
            parameters["hours"] = hours;
        var record = Record("backup_failed", key: "backup_failed:never") with { ItemIds = [], Params = parameters };

        Assert.Equal(body, NotificationTemplates.Render(record, KindOf("backup_failed", "platform"), new Dictionary<ItemId, string>()).Body);
    }
}
