using System.Text.Json;
using ModelContextProtocol.Client;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Host.IntegrationTests.Admin;
using Nid.Host.IntegrationTests.Audit;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Mcp;

/// <summary>Spec 4B §3: the read and document tools (<see cref="Expected"/>) and one per web write, the same JSON as REST, the same rights.</summary>
public class McpToolTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly string[] Expected =
    [
        "get_dashboard", "get_envelope", "get_envelope_holdings", "get_envelope_performance", "get_goal", "get_goals", "get_holding_performance", "get_income_source", "get_loan", "get_loan_schedule", "get_loans_overview",
        "get_net_worth_history", "get_passive_income", "get_property", "get_property_routine", "get_rent_arrears", "get_rent_revisions", "get_rental_2044", "get_routine", "get_sale_tax_estimate", "get_subscription", "list_envelopes",
        "list_bank_transactions", "list_expected_charges", "propose_bank_link", "list_holding_movements", "list_holding_snapshots", "list_income_sources", "list_loans", "list_documents", "list_persons", "list_properties", "list_subscriptions", "list_tax_years", "get_projection", "simulate_projection", "propose_amortization", "propose_holdings_snapshot", "propose_invoice", "propose_payslip", "propose_statement", "read_document", "simulate_early_repayment", "simulate_household_early_repayment", "upload_document",
    ];

    private static async Task<(NidApp App, McpClient Client)> StartAsync(PostgresFixture fixture)
    {
        var app = await NidApp.StartAsync(fixture);
        var read = await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Read);
        return (app, await McpHarness.ClientAsync(app, read.Secret));
    }

    private static async Task<string> CallAsync(McpClient client, string tool, object? args = null)
    {
        var arguments = args is null
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!;
        var result = await client.CallToolAsync(tool, arguments);
        Assert.True(result.IsError != true, $"{tool}: {McpHarness.TextOf(result)}");
        return McpHarness.TextOf(result);
    }

    private static void SameJson(string rest, string mcp)
        => Assert.Equal(JsonDocument.Parse(rest).RootElement.ToString(), JsonDocument.Parse(mcp).RootElement.ToString());

    [Fact]
    public async Task The_tool_list_is_the_read_tools_the_document_tools_and_one_per_web_write()
    {
        var (app, client) = await StartAsync(fixture);
        await using (app) await using (client)
        {
            var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(Expected.Concat(Nid.Host.Mcp.RouteActions.All.Select(a => a.Tool)).Order(StringComparer.Ordinal),
                tools.Select(t => t.Name).Order(StringComparer.Ordinal).ToArray());
            Assert.All(tools.Where(t => McpServerTests.IsRead(t.Name)), t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint));
        }
    }

    [Fact]
    public async Task Each_household_wide_tool_answers_as_its_rest_route()
    {
        var (app, client) = await StartAsync(fixture);
        await using (app) await using (client)
        {
            var a = app.AdultA;
            var subscription = await app.NewSubscriptionAsync("Netflix", a, Visibility.Shared);
            var envelope = await McpSeed.EnvelopeAsync(app, a);
            var income = await McpSeed.IncomeSourceAsync(app, a);
            var loan = await McpSeed.LoanAsync(app, a);
            var property = await McpSeed.PropertyAsync(app, a);

            SameJson(await app.GetRawAsync(a, "/api/v1/dashboard?scope=household"), await CallAsync(client, "get_dashboard", new { scope = "household" }));
            SameJson(await app.GetRawAsync(a, "/api/v1/dashboard/net-worth?scope=me&period=3y"), await CallAsync(client, "get_net_worth_history", new { scope = "me", period = "3y" }));
            SameJson(await app.GetRawAsync(a, "/api/v1/investments/overview/income?scope=household"), await CallAsync(client, "get_passive_income"));
            SameJson(await app.GetRawAsync(a, "/api/v1/investments/overview/income?scope=me"), await CallAsync(client, "get_passive_income", new { scope = "me" }));
            SameJson(await app.GetRawAsync(a, "/api/v1/investments/routine?on=2025-10-01"), await CallAsync(client, "get_routine", new { on = "2025-10-01" }));
            SameJson(await app.GetRawAsync(a, "/api/v1/realestate/routine"), await CallAsync(client, "get_property_routine"));
            SameJson(await app.GetRawAsync(a, "/api/v1/tax/2044?year=2025&scope=me"), await CallAsync(client, "get_rental_2044", new { year = 2025, scope = "me" }));
            SameJson(await app.GetRawAsync(a, "/api/v1/tax/sale?scope=me"), await CallAsync(client, "get_sale_tax_estimate", new { scope = "me" }));
            SameJson(await app.GetRawAsync(a, "/api/v1/subscriptions?scope=me"), await CallAsync(client, "list_subscriptions", new { scope = "me" }));
            SameJson(await app.GetRawAsync(a, "/api/v1/investments/envelopes?scope=household&archived=true"), await CallAsync(client, "list_envelopes", new { scope = "household", archived = true }));
            SameJson(await app.GetRawAsync(a, "/api/v1/income/sources"), await CallAsync(client, "list_income_sources"));
            SameJson(await app.GetRawAsync(a, "/api/v1/realestate/loans?ended=true"), await CallAsync(client, "list_loans", new { ended = true }));
            SameJson(await app.GetRawAsync(a, "/api/v1/realestate/properties"), await CallAsync(client, "list_properties"));
            SameJson(await app.GetRawAsync(a, "/api/v1/subscriptions?from=2025-09-01&to=2025-09-30&horizon=2026-01-01"),
                await CallAsync(client, "list_subscriptions", new { from = "2025-09-01", to = "2025-09-30", horizon = "2026-01-01" }));
            SameJson(await app.GetRawAsync(a, $"/api/v1/subscriptions/{subscription.Value}?from=2025-09-01&to=2025-09-30&horizon=2026-01-01"),
                await CallAsync(client, "get_subscription", new { id = subscription.Value, from = "2025-09-01", to = "2025-09-30", horizon = "2026-01-01" }));

            // The comparisons above cover data, not empty lists.
            Assert.Contains(envelope.ToString(), await CallAsync(client, "list_envelopes", new { scope = "household" }));
            Assert.Contains(loan.ToString(), await CallAsync(client, "list_loans"));
            Assert.Contains(income.ToString(), await CallAsync(client, "list_income_sources"));
            Assert.Contains(property.ToString(), await CallAsync(client, "list_properties"));
            Assert.Contains(subscription.Value.ToString(), await CallAsync(client, "list_subscriptions"));
        }
    }

    [Fact]
    public async Task Each_item_tool_answers_as_its_rest_route()
    {
        var (app, client) = await StartAsync(fixture);
        await using (app) await using (client)
        {
            var a = app.AdultA;
            var subscription = await app.NewSubscriptionAsync("Netflix", a, Visibility.Shared);
            var envelope = await McpSeed.EnvelopeAsync(app, a);
            var income = await McpSeed.IncomeSourceAsync(app, a);
            var loan = await McpSeed.LoanAsync(app, a);
            var property = await McpSeed.PropertyAsync(app, a);

            SameJson(await app.GetRawAsync(a, $"/api/v1/subscriptions/{subscription.Value}"), await CallAsync(client, "get_subscription", new { id = subscription.Value }));
            SameJson(await app.GetRawAsync(a, $"/api/v1/investments/envelopes/{envelope}"), await CallAsync(client, "get_envelope", new { id = envelope }));
            SameJson(await app.GetRawAsync(a, $"/api/v1/investments/envelopes/{envelope}/performance?from=2025-01-01&to=2025-10-01"),
                await CallAsync(client, "get_envelope_performance", new { id = envelope, from = "2025-01-01", to = "2025-10-01" }));
            var created = await app.RawAsync(a, HttpMethod.Post, $"/api/v1/investments/envelopes/{envelope}/holdings", new { name = "Fonds euros", kind = "euro_fund" });
            Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var holding = JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("id").GetGuid();
            SameJson(await app.GetRawAsync(a, $"/api/v1/investments/envelopes/{envelope}/holdings?on=2025-10-01"),
                await CallAsync(client, "get_envelope_holdings", new { id = envelope, on = "2025-10-01" }));
            SameJson(await app.GetRawAsync(a, $"/api/v1/investments/envelopes/{envelope}/holding-snapshots"), await CallAsync(client, "list_holding_snapshots", new { id = envelope }));
            SameJson(await app.GetRawAsync(a, $"/api/v1/investments/envelopes/{envelope}/holdings/{holding}/movements"),
                await CallAsync(client, "list_holding_movements", new { id = envelope, holdingId = holding }));
            SameJson(await app.GetRawAsync(a, $"/api/v1/investments/envelopes/{envelope}/holdings/{holding}/performance?from=2025-01-01&to=2025-10-01"),
                await CallAsync(client, "get_holding_performance", new { id = envelope, holdingId = holding, from = "2025-01-01", to = "2025-10-01" }));
            SameJson(await app.GetRawAsync(a, $"/api/v1/income/sources/{income}"), await CallAsync(client, "get_income_source", new { id = income }));
            SameJson(await app.GetRawAsync(a, $"/api/v1/realestate/loans/{loan}"), await CallAsync(client, "get_loan", new { id = loan }));
            SameJson(await app.GetRawAsync(a, $"/api/v1/realestate/loans/{loan}/schedule"), await CallAsync(client, "get_loan_schedule", new { id = loan }));
            SameJson(await app.GetRawAsync(a, $"/api/v1/realestate/properties/{property}"), await CallAsync(client, "get_property", new { id = property }));

            var rest = await app.RawAsync(a, HttpMethod.Post, $"/api/v1/realestate/loans/{loan}/simulations", new { on = "2026-01-15", amount = "10000.00" });
            SameJson(await rest.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), await CallAsync(client, "simulate_early_repayment", new { id = loan, on = "2026-01-15", amount = "10000.00" }));

            SameJson(await app.GetRawAsync(a, "/api/v1/realestate/loans/overview"), await CallAsync(client, "get_loans_overview"));
            var household = new { on = "2026-01-15", amount = "10000.00", split = "chosen", loanId = loan, effect = "reduce_term", penalties = new[] { new { loanId = loan, amount = "300.00" } } };
            var restHousehold = await app.RawAsync(a, HttpMethod.Post, "/api/v1/realestate/loans/overview/simulations", household);
            Assert.True(restHousehold.IsSuccessStatusCode, await restHousehold.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            SameJson(await restHousehold.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), await CallAsync(client, "simulate_household_early_repayment", household));
        }
    }

    // Review Focus 2.
    [Fact]
    public async Task A_malformed_id_is_an_error_not_a_crash()
    {
        var (app, client) = await StartAsync(fixture);
        await using (app) await using (client)
        {
            var result = await client.CallToolAsync("get_loan", new Dictionary<string, object?> { ["id"] = "abc" }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(result.IsError);
            // The SDK hides the binding detail: it names the tool, not the argument.
            Assert.Equal("An error occurred invoking 'get_loan'.", McpHarness.TextOf(result));
        }
    }

    [Fact]
    public async Task Another_adults_private_item_is_not_found_and_the_call_is_still_audited()
    {
        var (app, client) = await StartAsync(fixture);
        await using (app) await using (client)
        {
            var hidden = await app.NewSubscriptionAsync("Privé", app.AdultB, Visibility.Private);

            foreach (var id in new[] { hidden.Value, Guid.NewGuid() })
            {
                var result = await client.CallToolAsync("get_subscription", new Dictionary<string, object?> { ["id"] = id }, cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(result.IsError);
                Assert.Equal(McpCallsNotFound, McpHarness.TextOf(result));
            }

            Assert.Equal(2, (await AuditApi.EventsAsync(app, SecurityEventNames.McpToolCalled)).Count);
        }
    }

    private const string McpCallsNotFound = "Not found.";

    // Review Focus 3.
    [Theory]
    [InlineData("get_routine", "on", "15/10/2025", "15/10/2025")]
    [InlineData("get_dashboard", "scope", "tous", "tous")]
    [InlineData("get_net_worth_history", "period", "5y", "5y")]
    public async Task A_malformed_argument_is_a_tool_error_naming_it(string tool, string name, string value, string named)
    {
        var (app, client) = await StartAsync(fixture);
        await using (app) await using (client)
        {
            var result = await client.CallToolAsync(tool, new Dictionary<string, object?> { [name] = value }, cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.IsError);
            Assert.Contains(named, McpHarness.TextOf(result));
        }
    }

    [Fact]
    public async Task A_malformed_simulation_names_each_refused_field()
    {
        var (app, client) = await StartAsync(fixture);
        await using (app) await using (client)
        {
            var loan = await McpSeed.LoanAsync(app, app.AdultA);

            var result = await client.CallToolAsync("simulate_early_repayment",
                new Dictionary<string, object?> { ["id"] = loan, ["on"] = "15/01/2026", ["amount"] = "1 000,50" }, cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.IsError);
            Assert.Contains("on:", McpHarness.TextOf(result));
            Assert.Contains("amount:", McpHarness.TextOf(result));
        }
    }

    [Fact]
    public async Task A_malformed_household_simulation_names_each_refused_field()
    {
        var (app, client) = await StartAsync(fixture);
        await using (app) await using (client)
        {
            var result = await client.CallToolAsync("simulate_household_early_repayment", new Dictionary<string, object?>
            {
                ["on"] = "15/01/2026", ["amount"] = "1 000,50", ["split"] = "chosen", ["effect"] = "shorter",
            }, cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.IsError);
            foreach (var field in new[] { "on:", "amount:", "loanId:", "effect:" })
                Assert.Contains(field, McpHarness.TextOf(result));
        }
    }
}
