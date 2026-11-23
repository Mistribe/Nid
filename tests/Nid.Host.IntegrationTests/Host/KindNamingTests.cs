using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Application.Dashboard;
using Nid.Platform.Notifications;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>
/// Issue #514: a new notification or event kind is named <c>&lt;module&gt;.&lt;kind&gt;</c> in snake case, a notification kind's prefix being
/// the module it declares. The names below predate the rule: they are stored (notifications, deliveries, preferences keyed by kind, the
/// household Discord's once-per-key index) and on the wire, so they stay until a migration renames them. The list only shrinks.
/// </summary>
public partial class KindNamingTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly HashSet<string> Legacy =
    [
        "access_violation", "backup_failed", "card_unused", "childcare_below_minimum", "childcare_statement_due", "fx_rate_missing",
        "holding_entitlement", "holding_maturity", "idle_cash", "income_gap_soon", "income_phase_end", "irl_missing", "job_failed", "loan_end",
        "loan_phase_end", "login_locked", "month_changes", "net_worth_milestone", "password_changed", "password_link_issued", "password_reset",
        "per_ceiling_expiring", "per_deadline", "private_request_refused", "property_cost_due", "property_tax_due", "proposal_pending",
        "recovery_codes_low", "rent_arrears", "revision_due", "revision_lost", "routine_due", "savings_pace", "shortfall_ahead", "stale_value",
        "subscription_cancel_deadline", "subscription_expiry", "subscription_price_increase", "subscription_renewal", "subscription_trial_end",
        "sync_failed", "tax_balance_due", "tax_filing_deadline", "tax_milestone", "tax_new_rate", "tenancy_end", "token_expiring", "totp_reset",
        "uc_share_below_floor", "unusual_spending", "valuation_due", "valuation_stale",
    ];

    [GeneratedRegex("^[a-z]+(_[a-z]+)*\\.[a-z0-9]+(_[a-z0-9]+)*$")]
    private static partial Regex Prefixed();

    [Fact]
    public async Task Every_new_kind_is_prefixed_by_its_module()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var notifications = scope.ServiceProvider.GetRequiredService<NotificationCatalog>().Kinds.Values;
        var events = app.Services.GetRequiredService<UpcomingCatalog>().Kinds.Keys;

        Assert.All(notifications.Where(k => !Legacy.Contains(k.Kind)),
            k => Assert.True(Prefixed().IsMatch(k.Kind) && k.Kind.StartsWith(k.Module + ".", StringComparison.Ordinal), $"{k.Kind} ({k.Module})"));
        Assert.All(events.Where(k => !Legacy.Contains(k)), k => Assert.Matches(Prefixed(), k));
        // A legacy name gone (renamed with its rows) leaves the list.
        Assert.Empty(Legacy.Except(notifications.Select(k => k.Kind)).Except(events));
    }
}
