using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Platform.Notifications;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Platform.IntegrationTests;

/// <summary>Notifications design §4: a notification names the item and the date, never an amount.</summary>
public class NotificationTemplatesTests(SharedNidApp shared) : IClassFixture<SharedNidApp>
{
    private static readonly ItemId Item = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private static readonly (string Kind, string Module)[] Kinds =
    [
        ("subscriptions.renewal", "subscriptions"), ("subscriptions.expiry", "subscriptions"),
        ("subscriptions.cancel_deadline", "subscriptions"), ("subscriptions.trial_end", "subscriptions"), ("subscriptions.price_increase", "subscriptions"),
        ("subscriptions.ends_without_renewal", "subscriptions"),
        ("realestate.tenancy_end", "realestate"), ("realestate.property_cost_due", "realestate"), ("realestate.rent_arrears", "realestate"),
        ("realestate.revision_due", "realestate"), ("realestate.revision_lost", "realestate"), ("realestate.irl_missing", "realestate"),
        ("realestate.valuation_stale", "realestate"), ("realestate.property_tax_due", "realestate"),
        ("realestate.loan_phase_end", "realestate"), ("realestate.loan_end", "realestate"),
        ("investments.holding_maturity", "investments"), ("investments.stale_value", "investments"), ("investments.uc_share_below_floor", "investments"),
        ("income.phase_end", "income"), ("income.gap_soon", "income"),
        ("childcare.statement_due", "childcare"), ("childcare.below_minimum", "childcare"),
    ];

    private static NotificationRecord Record(string kind, LocalDate? dueOn = null, string key = "k")
        => new(Guid.NewGuid(), new PersonId(Guid.NewGuid()), kind, key, NotificationLevel.Info, [Item], dueOn, new LocalDate(2027, 3, 1),
            new Dictionary<string, string>(), NotificationStates.Open, null, null, Instant.FromUtc(2027, 3, 1, 0, 0), null, null);

    // Issue #514: each kind reads as its module declares it.
    private NotificationKind KindOf(string kind)
    {
        var scope = shared.App.Services.CreateAsyncScope();
        try
        {
            return scope.ServiceProvider.GetRequiredService<NotificationCatalog>().Find(kind) ?? throw new InvalidOperationException($"No kind {kind}.");
        }
        finally
        {
            scope.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [Theory]
    [InlineData("remote_on", null, "Les workers distants peuvent désormais recevoir les données du foyer.")]
    [InlineData("remote_off", null, "Les workers distants ne reçoivent plus les données du foyer.")]
    [InlineData("safe_on", "GPU box", "« GPU box » peut désormais recevoir les documents « local seulement ».")]
    [InlineData("safe_off", "GPU box", "« GPU box » ne reçoit plus les documents « local seulement ».")]
    [InlineData("safe_on", null, "Un worker peut désormais recevoir les documents « local seulement ».")]
    public void A_change_of_what_models_see_says_which_and_links_to_the_settings(string change, string? worker, string body)
    {
        var parameters = new Dictionary<string, string> { ["change"] = change };
        if (worker is not null)
            parameters["worker"] = worker;
        var record = Record("ai.data_policy_changed") with { ItemIds = [], Params = parameters };

        var rendered = NotificationTemplates.Render(record, KindOf("ai.data_policy_changed"),
            new Dictionary<ItemId, string>());

        Assert.Equal("Ce que voient les modèles a changé", rendered.Title);
        Assert.Equal(body, rendered.Body);
        Assert.Equal("/settings/ai", rendered.Link);
    }

    [Theory]
    [InlineData("ai.echo", "/admin/ai/jobs?status=dead")]
    [InlineData("fx-daily", "/admin/jobs/fx-daily")]
    public void A_failed_job_links_to_its_own_page(string job, string link)
    {
        var record = Record("platform.job_failed") with { ItemIds = [], Params = new Dictionary<string, string> { ["job"] = job } };

        var rendered = NotificationTemplates.Render(record, KindOf("platform.job_failed"),
            new Dictionary<ItemId, string>());

        Assert.Equal(link, rendered.Link);
    }

    [Fact]
    public void Every_kind_has_a_title_and_a_link()
    {
        foreach (var (kind, _) in Kinds)
        {
            var rendered = NotificationTemplates.Render(Record(kind), KindOf(kind), new Dictionary<ItemId, string> { [Item] = "Maison" });
            Assert.False(string.IsNullOrWhiteSpace(rendered.Title), kind);
            Assert.StartsWith("/", rendered.Link);
            Assert.Contains(Item.Value.ToString("D"), rendered.Link);
            Assert.DoesNotContain("Maison", rendered.Title);
        }
    }

    [Fact]
    public void Body_has_the_name_and_the_date_never_an_amount()
    {
        var rendered = NotificationTemplates.Render(Record("realestate.tenancy_end", new LocalDate(2027, 3, 31)), KindOf("realestate.tenancy_end"),
            new Dictionary<ItemId, string> { [Item] = "Maison" });

        Assert.Equal("Maison — le 31 mars 2027", rendered.Body);
        Assert.Equal("Un bail arrive à échéance", rendered.Title);
        Assert.Equal($"/properties/{Item.Value:D}?tab=rental", rendered.Link);
    }

    [Fact]
    public void Findings_name_their_month_or_date_and_a_missing_name_is_an_element()
    {
        var none = new Dictionary<ItemId, string>();
        var arrears = NotificationTemplates.Render(Record("realestate.rent_arrears", key: $"realestate.rent_arrears:{Item.Value:D}:2027-02"), KindOf("realestate.rent_arrears"),
            new Dictionary<ItemId, string> { [Item] = "Studio" });
        var stale = NotificationTemplates.Render(Record("realestate.valuation_stale", key: $"realestate.valuation_stale:{Item.Value:D}:2025-06-15"), KindOf("realestate.valuation_stale"), none);
        var loan = NotificationTemplates.Render(Record("realestate.loan_end", new LocalDate(2027, 12, 1)), KindOf("realestate.loan_end"), none);

        Assert.Equal("Studio — depuis février 2027", arrears.Body);
        var statement = NotificationTemplates.Render(Record("childcare.statement_due", new LocalDate(2025, 9, 30), $"childcare.statement_due:{Item.Value:D}:2025-09"),
            KindOf("childcare.statement_due"), new Dictionary<ItemId, string> { [Item] = "Garde de Léa" });
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
        var rate = NotificationTemplates.Render(Household("core.fx_rate_missing", "core.fx_rate_missing:USD", new() { ["currency"] = "USD" }), KindOf("core.fx_rate_missing"),
            new Dictionary<ItemId, string> { [Item] = "Maison" });
        var routine = NotificationTemplates.Render(Household("core.routine_due", "core.routine_due:2027-03", []), KindOf("core.routine_due"), new Dictionary<ItemId, string>());

        Assert.Equal(("Un taux de change manque", "Devise USD", "/dashboard"), (rate.Title, rate.Body, rate.Link));
        Assert.Equal(("La routine du mois est à faire", "Des valeurs sont à mettre à jour", "/routine"), (routine.Title, routine.Body, routine.Link));
    }

    [Fact]
    public void The_month_changes_name_directions_and_causes_never_an_amount()
    {
        var record = Record("core.month_changes", key: "core.month_changes:2027-03") with
        {
            ItemIds = [],
            Params = new Dictionary<string, string> { ["income"] = "down", ["net_worth"] = "up|investments.value+|real_estate.paid_in+", ["commitments"] = "up|subscriptions+|unknown-" },
        };

        var rendered = NotificationTemplates.Render(record, KindOf("core.month_changes"), new Dictionary<ItemId, string>());

        Assert.Equal(("Ce qui a changé depuis le mois dernier",
            "Patrimoine en hausse — valeur des placements ↑, capital remboursé ↑ · Engagements en hausse — abonnements ↑, autres ↓ · Revenus en baisse",
            "/dashboard"), (rendered.Title, rendered.Body, rendered.Link));
    }

    [Fact]
    public void The_tax_calendar_opens_the_tax_page_and_names_the_day()
    {
        RenderedNotification Tax(string kind, Dictionary<string, string> parameters) => NotificationTemplates.Render(
            Record(kind, new LocalDate(2026, 5, 21), $"{kind}:2026-05-21") with { ItemIds = [], Params = parameters }, KindOf(kind), new Dictionary<ItemId, string>());

        Assert.Equal(("La déclaration de revenus est à faire", "En ligne avant le 21 mai 2026", "/tax"),
            (Tax("tax.filing_deadline", []).Title, Tax("tax.filing_deadline", []).Body, Tax("tax.filing_deadline", []).Link));
        Assert.Equal("En ligne avant le 21 mai 2026, la date la plus tôt : département non renseigné",
            Tax("tax.filing_deadline", new() { ["note"] = "department_unknown" }).Body);
        Assert.Equal(("À partir du 21 mai 2026", "/tax"), (Tax("tax.balance_due", []).Body, Tax("tax.new_rate", []).Link));
        Assert.Equal("/tax?tab=withholding", Tax("tax.balance_due", []).Link);
    }

    [Fact]
    public void A_failed_job_opens_its_own_page()
    {
        NotificationRecord Job(Dictionary<string, string> parameters) => Record("platform.job_failed", key: "platform.job_failed:fx-daily") with { ItemIds = [], Params = parameters };

        Assert.Equal("/admin/jobs/fx-daily", NotificationTemplates.Render(Job(new() { ["job"] = "fx-daily" }), KindOf("platform.job_failed"), new Dictionary<ItemId, string>()).Link);
        Assert.Equal("/admin/jobs", NotificationTemplates.Render(Job([]), KindOf("platform.job_failed"), new Dictionary<ItemId, string>()).Link);
    }

    [Fact]
    public void Investments_and_income_open_their_screens_and_name_the_date()
    {
        var names = new Dictionary<ItemId, string> { [Item] = "Assurance-vie" };
        var maturity = NotificationTemplates.Render(Record("investments.holding_maturity", new LocalDate(2027, 12, 1)), KindOf("investments.holding_maturity"), names);
        var stale = NotificationTemplates.Render(Record("investments.stale_value", key: $"investments.stale_value:{Item.Value:D}:2025-06-15"), KindOf("investments.stale_value"), names);
        var floor = NotificationTemplates.Render(Record("investments.uc_share_below_floor", key: $"investments.uc_share_below_floor:{Item.Value:D}:2024-12-31"), KindOf("investments.uc_share_below_floor"), names);
        var phase = NotificationTemplates.Render(Record("income.phase_end", new LocalDate(2027, 3, 31)), KindOf("income.phase_end"), names);
        var gap = NotificationTemplates.Render(Record("income.gap_soon", new LocalDate(2027, 3, 31)), KindOf("income.gap_soon"), names);

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

            var rendered = NotificationTemplates.Render(record, KindOf(kind), new Dictionary<ItemId, string> { [Item] = "Maison" });

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
            (Published("platform.access_violation", "platform.access_violation:1", new() { ["request"] = "GetEnvelope", ["admin"] = "1" }), "Une réponse a été bloquée par la garde d'accès", "/admin/security"),
            (Published("platform.access_violation", "platform.access_violation:1", new() { ["request"] = "GetEnvelope" }), "Une réponse a été bloquée par la garde d'accès", "/"),
            (Published("core.token_expiring", "core.token_expiring:abc:2027-03-31"), "Un jeton d'accès expire bientôt", "/security"),
            (Published("banking.consent_expiring", "banking.consent_expiring:abc:2027-03-31:14", new() { ["bank"] = "Banque Alpha" }), "Le consentement d'une banque expire bientôt", "/settings/banks"),
            (Published("banking.consent_expired", "banking.consent_expired:abc:2027-03-31"), "Le consentement d'une banque a expiré", "/settings/banks"),
            (Published("banking.sync_failing", "banking.sync_failing:abc:2027-03-01"), "La synchronisation d'une banque échoue", "/settings/banks"),
            (Published("banking.application_needed", "banking.application_needed:abc"), "Reliez à nouveau votre banque", "/settings/banks?tab=application"),
            (Published("core.proposal_pending", $"core.proposal_pending:{proposal}", withItem: true), "Une proposition attend votre décision", $"/proposals/{proposal}"),
            (Published("core.private_request_refused", $"core.private_request_refused:{proposal}", withItem: true), "Votre demande de passage en privé a été refusée", $"/proposals/{proposal}"),
            (Published("platform.backup_failed", "platform.backup_failed:never"), "La sauvegarde a échoué", "/admin/system"),
            (Published("platform.job_failed", "platform.job_failed:fx-sync", new() { ["job"] = "fx-sync" }), "Une tâche planifiée a échoué", "/admin/jobs/fx-sync"),
            (Published("platform.sync_failed", "platform.sync_failed:prices:never", new() { ["source"] = "prices" }), "Une synchronisation a échoué", "/admin/prices"),
            // #291: the holder of a local account, sent to their Security page.
            (Published("platform.login_locked", "platform.login_locked:abc:1"), "Votre compte a été verrouillé", "/security"),
            (Published("platform.password_link_issued", "platform.password_link_issued:abc:1"), "Un lien de réinitialisation a été créé pour vous", "/security"),
            (Published("platform.password_reset", "platform.password_reset:abc:1"), "Votre mot de passe a été réinitialisé", "/security"),
            (Published("platform.password_changed", "platform.password_changed:abc:1", new() { ["field"] = "password" }), "Votre mot de passe a changé", "/security"),
            (Published("platform.totp_reset", "platform.totp_reset:abc:1"), "Votre validation en deux étapes a été désactivée", "/security"),
            (Published("platform.recovery_codes_low", "platform.recovery_codes_low:abc"), "Il vous reste 2 codes de secours", "/security"),
        };
        foreach (var (record, title, link) in cases)
        {
            var rendered = NotificationTemplates.Render(record, KindOf(record.Kind), names);
            Assert.Equal((title, link), (rendered.Title, rendered.Link));
            if (link == "/security" && record.Kind != "core.token_expiring") Assert.NotEmpty(rendered.Body);
            Assert.DoesNotContain("Maison", rendered.Body);
            Assert.DoesNotMatch(@"\d+\s?(€|EUR)", rendered.Body);
        }

        Assert.Equal("Requête : GetEnvelope", NotificationTemplates.Render(cases[0].Item1, KindOf("platform.access_violation"), names).Body);
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
        var record = Record("platform.backup_failed", key: "platform.backup_failed:never") with { ItemIds = [], Params = parameters };

        Assert.Equal(body, NotificationTemplates.Render(record, KindOf("platform.backup_failed"), new Dictionary<ItemId, string>()).Body);
    }

    [Fact]
    public void The_centre_shows_a_signals_figures_the_french_way()
    {
        var record = Record("core.net_worth_milestone", key: "core.net_worth_milestone:300000") with
        {
            ItemIds = [],
            Params = new Dictionary<string, string> { ["net_worth_amount"] = "302140.40", ["step_amount"] = "300000" },
        };
        var kind = KindOf("core.net_worth_milestone");

        var centre = NotificationTemplates.Render(record, kind, new Dictionary<ItemId, string>(), withFigures: true);
        var plain = NotificationTemplates.Render(record, kind, new Dictionary<ItemId, string>());

        Assert.Equal("Votre patrimoine a franchi un palier", centre.Title);
        Assert.Equal("Patrimoine : 302 140 € · Palier : 300 000 €", centre.Body.Replace('\u202f', ' ').Replace('\u00a0', ' '));
        Assert.Equal("/dashboard", centre.Link);
        Assert.Equal("", plain.Body);

        var swiss = record with { Params = new Dictionary<string, string>(record.Params) { ["currency"] = "CHF" } };
        Assert.EndsWith("300 000 CHF", NotificationTemplates.Render(swiss, kind, new Dictionary<ItemId, string>(), withFigures: true).Body.Replace('\u202f', ' ').Replace('\u00a0', ' '));
    }

    [Fact]
    public void A_shortfall_opens_its_accounts_operations_and_names_its_charges_in_the_centre_only()
    {
        var figures = new Dictionary<string, string>
        {
            ["balance_amount"] = "800.00", ["charges_amount"] = "1150.50", ["currency"] = "EUR", ["short_on"] = "2027-03-05",
            ["charges"] = "Loyer, Netflix", ["module"] = "investments",
        };
        var record = Record("banking.shortfall_ahead", new LocalDate(2027, 3, 5)) with { Params = figures };
        var kind = KindOf("banking.shortfall_ahead");
        var names = new Dictionary<ItemId, string> { [Item] = "Compte joint" };

        var centre = NotificationTemplates.Render(record, kind, names, withFigures: true);
        var plain = NotificationTemplates.Render(record, kind, names);

        Assert.Equal("Compte joint — le 5 mars 2027 — Solde : 800 € · Charges à venir : 1 151 € · Ce jour-là : Loyer, Netflix",
            centre.Body.Replace('\u202f', ' ').Replace('\u00a0', ' '));
        Assert.Equal("Compte joint — le 5 mars 2027", plain.Body);
        Assert.Equal($"/investments/{Item.Value:D}?tab=operations", centre.Link);

        // Without an envelope the account has no item to be named by: its own name follows the date.
        var unassociated = record with { ItemIds = [], Params = new Dictionary<string, string>(figures.Where(p => p.Key != "module")) { ["account"] = "Compte courant" } };
        Assert.Equal(("Le 5 mars 2027 — Compte courant", "/settings/banks"), (NotificationTemplates.Render(unassociated, kind, names) is var r ? (r.Body, r.Link) : default));
    }

    [Fact]
    public void An_unused_card_is_named_by_its_name_and_masked_number_and_opens_the_banks()
    {
        var record = Record("banking.card_unused", key: "banking.card_unused:x:2027-03") with
        {
            ItemIds = [], DueOn = new LocalDate(2027, 3, 31),
            Params = new Dictionary<string, string> { ["card"] = "Carte Visa", ["number"] = "4321", ["count"] = "0", ["min"] = "1" },
        };
        var kind = KindOf("banking.card_unused");

        var rendered = NotificationTemplates.Render(record, kind, new Dictionary<ItemId, string>());

        Assert.Equal("Une carte n'a pas assez servi ce mois-ci", rendered.Title);
        Assert.Equal("Carte Visa •••• 4321 : aucun paiement ce mois-ci, 1 attendu. Utilisez-la avant le 31 mars 2027.", rendered.Body);
        Assert.Equal("/settings/banks", rendered.Link);
        Assert.StartsWith("Une carte : 2 paiements ce mois-ci, 3 attendus.", NotificationTemplates.Render(record with
            { Params = new Dictionary<string, string> { ["card"] = "", ["number"] = "", ["count"] = "2", ["min"] = "3" } }, kind, new Dictionary<ItemId, string>()).Body);
    }

    [Fact]
    public void Idle_cash_says_what_held_and_opens_the_investments()
    {
        var record = Record("banking.idle_cash", key: "banking.idle_cash:2027-03") with
        {
            ItemIds = [],
            Params = new Dictionary<string, string>
            {
                ["above_amount"] = "4200.40", ["buffer_amount"] = "6000.00", ["room_amount"] = "2000", ["currency"] = "EUR", ["reason"] = "room_goal_behind",
            },
        };
        var kind = KindOf("banking.idle_cash");

        var centre = NotificationTemplates.Render(record, kind, new Dictionary<ItemId, string>(), withFigures: true);
        var plain = NotificationTemplates.Render(record, kind, new Dictionary<ItemId, string>());

        Assert.Equal("Vos comptes courants dépassent l'épargne de précaution", centre.Title);
        Assert.Equal("Au-dessus de l'épargne de précaution depuis 4 semaines, un livret a de la place et un objectif est en retard", plain.Body);
        Assert.EndsWith("Au-dessus de l'épargne de précaution : 4 200 € · Épargne de précaution : 6 000 € · Place sur les livrets : 2 000 €",
            centre.Body.Replace('\u202f', ' ').Replace('\u00a0', ' '));
        Assert.Equal("/investments", centre.Link);
        Assert.EndsWith(", un objectif est en retard",
            NotificationTemplates.Render(record with { Params = new Dictionary<string, string> { ["reason"] = "goal_behind" } }, kind, new Dictionary<ItemId, string>()).Body);
    }

    [Fact]
    public void Unusual_spending_names_the_category_keeps_figures_and_lines_in_the_centre_and_opens_its_operations()
    {
        var record = Record("banking.unusual_spending", key: "banking.unusual_spending:x:2027-02") with
        {
            ItemIds = [],
            Params = new Dictionary<string, string>
            {
                ["category"] = "22222222-2222-2222-2222-222222222222", ["name"] = "Courses", ["month"] = "2027-02", ["reason"] = "usual",
                ["spent_amount"] = "420.00", ["usual_amount"] = "250.00", ["currency"] = "EUR", ["lines"] = "CB MARCHE 120,00, CB EPICERIE",
            },
        };
        var kind = KindOf("banking.unusual_spending");

        var centre = NotificationTemplates.Render(record, kind, new Dictionary<ItemId, string>(), withFigures: true);
        var plain = NotificationTemplates.Render(record, kind, new Dictionary<ItemId, string>());

        Assert.Equal("Des dépenses sortent de l'ordinaire", plain.Title);
        Assert.Equal("Courses : bien au-dessus du mois habituel", plain.Body); // e-mail and Discord: no amount, no bank label
        Assert.EndsWith("Dépensé ce mois : 420 € · Mois habituel : 250 € · Plus grosses dépenses : CB MARCHE 120,00, CB EPICERIE",
            centre.Body.Replace(' ', ' ').Replace(' ', ' '));
        Assert.Equal("/spending?tab=operations&category=22222222-2222-2222-2222-222222222222&from=2027-02-01&to=2027-02-28&state=counted", plain.Link);
        Assert.Equal("Une catégorie : le budget sera dépassé au rythme actuel",
            NotificationTemplates.Render(record with { Params = new Dictionary<string, string> { ["reason"] = "budget_pace" } }, kind, new Dictionary<ItemId, string>()).Body);
        Assert.Equal("/spending", NotificationTemplates.Render(record with { Params = new Dictionary<string, string>() }, kind, new Dictionary<ItemId, string>()).Link);
    }
}
