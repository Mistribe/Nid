using System.Text.Json;
using ModelContextProtocol.Client;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.IntegrationTests.Admin;
using Nid.Subscriptions.IntegrationTests.Audit;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Mcp;

/// <summary>Spec 4B §3: the 18 tools, the same JSON as REST, the same rights.</summary>
[Collection("postgres")]
public class McpToolTests(PostgresFixture fixture)
{
    private static readonly string[] Expected =
    [
        "get_dashboard", "get_envelope", "get_envelope_performance", "get_income_source", "get_loan", "get_loan_schedule",
        "get_net_worth_history", "get_property", "get_property_routine", "get_routine", "get_subscription", "list_envelopes",
        "list_income_sources", "list_loans", "list_persons", "list_properties", "list_subscriptions", "simulate_early_repayment",
    ];

    private static async Task<(NidApp App, McpClient Client)> StartAsync(PostgresFixture fixture)
    {
        var app = await NidApp.StartAsync(fixture);
        var read = await TokenRouteTests.CreateAsync(app, app.AdultA, TokenPresets.Read);
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
    public async Task The_tool_list_is_exactly_the_eighteen_read_tools()
    {
        var (app, client) = await StartAsync(fixture);
        await using (app) await using (client)
        {
            var tools = await client.ListToolsAsync();
            Assert.Equal(Expected, tools.Select(t => t.Name).Order(StringComparer.Ordinal).ToArray());
            Assert.All(tools, t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint));
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
            SameJson(await app.GetRawAsync(a, "/api/v1/investments/routine?on=2025-10-01"), await CallAsync(client, "get_routine", new { on = "2025-10-01" }));
            SameJson(await app.GetRawAsync(a, "/api/v1/realestate/routine"), await CallAsync(client, "get_property_routine"));
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
            SameJson(await app.GetRawAsync(a, $"/api/v1/income/sources/{income}"), await CallAsync(client, "get_income_source", new { id = income }));
            SameJson(await app.GetRawAsync(a, $"/api/v1/realestate/loans/{loan}"), await CallAsync(client, "get_loan", new { id = loan }));
            SameJson(await app.GetRawAsync(a, $"/api/v1/realestate/loans/{loan}/schedule"), await CallAsync(client, "get_loan_schedule", new { id = loan }));
            SameJson(await app.GetRawAsync(a, $"/api/v1/realestate/properties/{property}"), await CallAsync(client, "get_property", new { id = property }));

            var rest = await app.RawAsync(a, HttpMethod.Post, $"/api/v1/realestate/loans/{loan}/simulations", new { on = "2026-01-15", amount = "10000.00" });
            SameJson(await rest.Content.ReadAsStringAsync(), await CallAsync(client, "simulate_early_repayment", new { id = loan, on = "2026-01-15", amount = "10000.00" }));
        }
    }

    // Review Focus 2.
    [Fact]
    public async Task Another_adults_private_item_is_not_found_and_the_call_is_still_audited()
    {
        var (app, client) = await StartAsync(fixture);
        await using (app) await using (client)
        {
            var hidden = await app.NewSubscriptionAsync("Privé", app.AdultB, Visibility.Private);

            foreach (var id in new[] { hidden.Value, Guid.NewGuid() })
            {
                var result = await client.CallToolAsync("get_subscription", new Dictionary<string, object?> { ["id"] = id });
                Assert.True(result.IsError);
                Assert.Equal(McpCallsNotFound, McpHarness.TextOf(result));
            }

            Assert.Equal(2, (await SecurityEventTests.EventsAsync(app, SecurityEventNames.McpToolCalled)).Count);
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
            var result = await client.CallToolAsync(tool, new Dictionary<string, object?> { [name] = value });

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
                new Dictionary<string, object?> { ["id"] = loan, ["on"] = "15/01/2026", ["amount"] = "1 000,50" });

            Assert.True(result.IsError);
            Assert.Contains("on:", McpHarness.TextOf(result));
            Assert.Contains("amount:", McpHarness.TextOf(result));
        }
    }
}

/// <summary>One item per module, through the real routes, with the bodies the module host tests use.</summary>
internal static class McpSeed
{
    private static async Task<Guid> IdOf(HttpResponseMessage response)
    {
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    public static async Task<Guid> EnvelopeAsync(NidApp app, PersonId owner)
        => await IdOf(await app.RawAsync(owner, HttpMethod.Post, "/api/v1/investments/envelopes", new
        {
            displayName = "PEA", kind = "pea", currency = "EUR", visibility = "shared",
            owners = new[] { new { personId = owner.Value, shareBp = 10_000 } },
            institution = "Banque", accountHint = "4521", valuationCadence = "monthly",
            opening = new { occurredOn = "2025-01-31", amount = new { amount = "20000.00", currency = "EUR" } },
            firstValuation = new { asOf = "2025-01-31", amount = new { amount = "21000.00", currency = "EUR" } },
        }));

    public static async Task<Guid> IncomeSourceAsync(NidApp app, PersonId owner)
        => await IdOf(await app.RawAsync(owner, HttpMethod.Post, "/api/v1/income/sources", new Dictionary<string, object?>
        {
            ["displayName"] = "Salaire", ["kind"] = "salary", ["currency"] = "EUR", ["visibility"] = "shared",
            ["owners"] = new[] { new { personId = owner.Value, shareBp = 10_000 } },
            ["phases"] = new object[]
            {
                new { label = "Salaire", startsOn = "2025-01-01", endsOn = (string?)null,
                      netAmount = new { amount = "2300", currency = "EUR" }, every = new { count = 1, unit = "month" } },
            },
        }));

    public static Task<Guid> LoanAsync(NidApp app, PersonId owner)
        => RealEstate.LoanApi.NewAsync(app, RealEstate.LoanApi.Golden(app, "Prêt maison", "shared", owner), owner);

    public static Task<Guid> PropertyAsync(NidApp app, PersonId owner)
        => RealEstate.PropertyApi.NewPropertyAsync(app, RealEstate.PropertyApi.Home(app, "Maison", "shared", owner), owner);
}
