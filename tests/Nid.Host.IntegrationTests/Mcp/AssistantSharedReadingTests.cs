using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Ai.Application;
using Nid.Core.Contracts;
using Nid.Host.Assistant;
using Nid.Host.IntegrationTests.LeakBench;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Mcp;

/// <summary>#533, decision C (<c>docs/ai/README.md</c> §6.3, §6.4): off the premises, the person's own items go (they turned
/// the assistant on), an item one of whose owners chose <c>never</c> is held back in every allowed read, and no tool trips the
/// output guard doing so.</summary>
public sealed class AssistantSharedReadingTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Off_the_premises_no_allowed_tool_shows_an_item_whose_owner_chose_never()
    {
        var data = await MixedDataset.CreateAsync(fixture);
        await using var app = data.App;
        var tools = app.Services.GetRequiredService<IAssistantTools>();
        var calls = Calls(data);
        Assert.Equal(AssistantCatalogue.Allowed.Keys.Where(t => !t.StartsWith("propose_", StringComparison.Ordinal)).Order(), calls.Keys.Order());
        // Bob chose never: what he owns, alone or with Alice, stays home; Alice's own private items go.
        var bobs = IdsOf(data, "S-B", "J-AB");
        var alices = IdsOf(data, "P-A");
        var neverBob = new OffsiteReading(new HashSet<PersonId> { app.AdultB });

        var offsite = new List<string>();
        var onPremises = new List<string>();
        foreach (var (tool, args) in calls)
        {
            var text = (await tools.CallAsync(app.AdultA, neverBob, tool, JsonSerializer.SerializeToElement(args), 1_000_000, TestContext.Current.CancellationToken)).Text;
            Assert.All(bobs, id => Assert.False(text.Contains(id, StringComparison.Ordinal), $"{tool}: {id}"));
            offsite.Add(text);
            onPremises.Add((await tools.CallAsync(app.AdultA, null, tool, JsonSerializer.SerializeToElement(args), 1_000_000, TestContext.Current.CancellationToken)).Text);
        }

        Assert.All(alices, id => Assert.Contains(offsite, text => text.Contains(id, StringComparison.Ordinal)));
        // The control: on the premises, the same calls show Bob's items, so the check above can fail.
        Assert.All(bobs, id => Assert.Contains(onPremises, text => text.Contains(id, StringComparison.Ordinal)));
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        Assert.Equal(0, await platform.Notifications.CountAsync(n => n.Kind == "platform.access_violation", TestContext.Current.CancellationToken));
    }

    /// <summary>The ids of the named rows in every module the dataset fills.</summary>
    private static List<string> IdsOf(MixedDataset data, params string[] names)
        => [.. new[] { data.Items, data.Envelopes, data.IncomeSources, data.Loans, data.Properties, data.Goals, data.Assets }
            .SelectMany(module => names.Where(module.ContainsKey).Select(name => module[name].Value.ToString()))];

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
        ["list_assets"] = new { scope = "me" },
        ["get_asset"] = new { id = data.Assets["P-A"].Value },
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
