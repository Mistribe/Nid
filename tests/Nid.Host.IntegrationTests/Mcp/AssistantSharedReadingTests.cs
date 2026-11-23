using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Ai.Application;
using Nid.Host.Assistant;
using Nid.Host.IntegrationTests.LeakBench;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Mcp;

/// <summary>#500, decision C (<c>docs/ai/README.md</c> §6.4): a turn that goes off the premises reads shared items only, its
/// person's own private ones included in what it does not see, and no tool trips the output guard doing so.</summary>
public sealed class AssistantSharedReadingTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Off_the_premises_no_allowed_tool_shows_the_persons_own_private_items()
    {
        var data = await MixedDataset.CreateAsync(fixture);
        await using var app = data.App;
        var tools = app.Services.GetRequiredService<IAssistantTools>();
        var calls = Calls(data);
        Assert.Equal(AssistantCatalogue.Allowed.Keys.Where(t => !t.StartsWith("propose_", StringComparison.Ordinal)).Order(), calls.Keys.Order());
        var privateIds = new[] { data["P-A"], data.Envelopes["P-A"], data.IncomeSources["P-A"], data.Loans["P-A"], data.Properties["P-A"], data.Goals["P-A"] }
            .Select(i => i.Value.ToString()).ToList();

        var onPremises = new List<string>();
        foreach (var (tool, args) in calls)
        {
            var offsite = await tools.CallAsync(app.AdultA, true, tool, JsonSerializer.SerializeToElement(args), 1_000_000, TestContext.Current.CancellationToken);
            Assert.False(offsite.Text.Contains(MixedDataset.PrivateLabelA, StringComparison.Ordinal), $"{tool}: {offsite.Text}");
            Assert.All(privateIds, id => Assert.False(offsite.Text.Contains(id, StringComparison.Ordinal), $"{tool}: {id}"));
            onPremises.Add((await tools.CallAsync(app.AdultA, false, tool, JsonSerializer.SerializeToElement(args), 1_000_000, TestContext.Current.CancellationToken)).Text);
        }

        // The control: on the premises, the same calls do show them, so the checks above can fail.
        Assert.Contains(onPremises, text => text.Contains(MixedDataset.PrivateLabelA, StringComparison.Ordinal));
        Assert.All(privateIds, id => Assert.Contains(onPremises, text => text.Contains(id, StringComparison.Ordinal)));
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        Assert.Equal(0, await platform.Notifications.CountAsync(n => n.Kind == "access_violation", TestContext.Current.CancellationToken));
    }

    /// <summary>Each allowed read, aimed at A's private item of its module or in A's widest scope.</summary>
    private static Dictionary<string, object> Calls(MixedDataset data) => new()
    {
        ["get_dashboard"] = new { scope = "me" },
        ["get_net_worth_history"] = new { scope = "me" },
        ["get_month_changes"] = new { scope = "me" },
        ["get_spending"] = new { scope = "me" },
        ["list_envelopes"] = new { scope = "me" },
        ["get_envelope"] = new { id = data.Envelopes["P-A"].Value },
        ["get_envelope_performance"] = new { id = data.Envelopes["P-A"].Value, from = "2024-01-01", to = "2025-12-31" },
        ["list_loans"] = new { scope = "me" },
        ["get_loan"] = new { id = data.Loans["P-A"].Value },
        ["get_loans_overview"] = new { scope = "me" },
        ["list_properties"] = new { scope = "me" },
        ["get_property"] = new { id = data.Properties["P-A"].Value },
        ["list_income_sources"] = new { scope = "me" },
        ["get_income_source"] = new { id = data.IncomeSources["P-A"].Value },
        ["list_tax_years"] = new { scope = "me" },
        ["get_tax"] = new { scope = "me" },
        ["get_goals"] = new { scope = "me" },
        ["get_goal"] = new { id = data.Goals["P-A"].Value, scope = "me" },
        ["list_subscriptions"] = new { scope = "me" },
        ["get_subscription"] = new { id = data["P-A"].Value },
        ["list_persons"] = new { },
        ["list_proposals"] = new { },
        ["list_expected_charges"] = new { from = "2025-01-01", to = "2025-03-31" },
        ["list_bank_transactions"] = new { state = "all" },
        ["list_uncategorised_bank_transactions"] = new { },
    };
}
