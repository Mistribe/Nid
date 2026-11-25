using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Platform.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// #645: the legacy notification kinds take their module's prefix (<c>loan_end</c> becomes <c>realestate.loan_end</c>) in every row keyed by
    /// kind: a notification's kind and the head of its key (<c>{kind}:…</c>), a household delivery's key, and a person's lead days and
    /// signals. <c>Down</c> puts the old names back.
    /// </summary>
    public partial class PrefixedNotificationKinds645 : Migration
    {
        /// <summary>The legacy name and its prefixed one; never edited once released.</summary>
        public static readonly (string Old, string New)[] Renames =
        [
            ("subscription_renewal", "subscriptions.renewal"),
            ("subscription_expiry", "subscriptions.expiry"),
            ("subscription_cancel_deadline", "subscriptions.cancel_deadline"),
            ("subscription_trial_end", "subscriptions.trial_end"),
            ("subscription_price_increase", "subscriptions.price_increase"),
            ("income_phase_end", "income.phase_end"),
            ("income_gap_soon", "income.gap_soon"),
            ("loan_phase_end", "realestate.loan_phase_end"),
            ("loan_end", "realestate.loan_end"),
            ("valuation_due", "realestate.valuation_due"),
            ("property_cost_due", "realestate.property_cost_due"),
            ("tenancy_end", "realestate.tenancy_end"),
            ("property_tax_due", "realestate.property_tax_due"),
            ("holding_maturity", "investments.holding_maturity"),
            ("holding_entitlement", "investments.holding_entitlement"),
            ("tax_milestone", "investments.tax_milestone"),
            ("per_deadline", "tax.per_deadline"),
            ("per_ceiling_expiring", "tax.per_ceiling_expiring"),
            ("tax_filing_deadline", "tax.filing_deadline"),
            ("tax_new_rate", "tax.new_rate"),
            ("tax_balance_due", "tax.balance_due"),
            ("access_violation", "platform.access_violation"),
            ("backup_failed", "platform.backup_failed"),
            ("job_failed", "platform.job_failed"),
            ("sync_failed", "platform.sync_failed"),
            ("login_locked", "platform.login_locked"),
            ("password_link_issued", "platform.password_link_issued"),
            ("password_reset", "platform.password_reset"),
            ("password_changed", "platform.password_changed"),
            ("totp_reset", "platform.totp_reset"),
            ("recovery_codes_low", "platform.recovery_codes_low"),
            ("proposal_pending", "core.proposal_pending"),
            ("private_request_refused", "core.private_request_refused"),
            ("fx_rate_missing", "core.fx_rate_missing"),
            ("routine_due", "core.routine_due"),
            ("token_expiring", "core.token_expiring"),
            ("month_changes", "core.month_changes"),
            ("net_worth_milestone", "core.net_worth_milestone"),
            ("savings_pace", "core.savings_pace"),
            ("childcare_statement_due", "childcare.statement_due"),
            ("childcare_below_minimum", "childcare.below_minimum"),
            ("stale_value", "investments.stale_value"),
            ("uc_share_below_floor", "investments.uc_share_below_floor"),
            ("rent_arrears", "realestate.rent_arrears"),
            ("valuation_stale", "realestate.valuation_stale"),
            ("revision_due", "realestate.revision_due"),
            ("revision_lost", "realestate.revision_lost"),
            ("irl_missing", "realestate.irl_missing"),
            ("card_unused", "banking.card_unused"),
            ("idle_cash", "banking.idle_cash"),
            ("shortfall_ahead", "banking.shortfall_ahead"),
            ("unusual_spending", "banking.unusual_spending"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Rename(Renames));

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.Sql(Rename([.. Renames.Select(r => (r.New, r.Old))]));

        /// <summary>A key renamed is one that starts with the kind and a colon: no name holds a colon, so no key matches two.</summary>
        private static string Rename(IReadOnlyList<(string From, string To)> renames)
        {
            var map = $"with map(old, new) as (values {string.Join(", ", renames.Select(r => $"('{r.From}', '{r.To}')"))})";
            return $$"""
                {{map}}
                update platform.notification n set kind = map.new from map where n.kind = map.old;
                {{map}}
                update platform.notification n set key = map.new || substr(n.key, length(map.old) + 1) from map
                where left(n.key, length(map.old) + 1) = map.old || ':';
                {{map}}
                update platform.notification_delivery d set household_key = map.new || substr(d.household_key, length(map.old) + 1) from map
                where left(d.household_key, length(map.old) + 1) = map.old || ':';
                {{map}}
                update platform.notification_preferences p set
                    lead_days = (select coalesce(jsonb_object_agg(coalesce(m.new, e.key), e.value), '{}'::jsonb)
                                 from jsonb_each(p.lead_days) e left join map m on m.old = e.key),
                    signals = (select coalesce(jsonb_object_agg(coalesce(m.new, e.key), e.value), '{}'::jsonb)
                               from jsonb_each(p.signals) e left join map m on m.old = e.key);
                """;
        }
    }
}
