using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Nid.Core.Contracts;
using Nid.Host.Mcp;
using Nid.Subscriptions.IntegrationTests.Admin;
using Nid.Subscriptions.IntegrationTests.Proposals;
using Nid.Subscriptions.IntegrationTests.RealEstate;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Mcp;

/// <summary>Spec 4D3: every web write as a tool, proposed by default and approved by replaying its route.</summary>
public class McpRouteToolTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    /// <summary>The writes no tool proposes, on purpose (<see cref="RouteActions"/>' summary).</summary>
    private static readonly string[] NotProposed =
    [
        "/api/v1/admin/", "/api/v1/platform/imports", "/api/v1/core/proposals", "/api/v1/core/me/", "/api/v1/core/fx-rates/",
        "/api/v1/subscriptions/catalog/sync", "/api/v1/investments/prices/sync", "/price/accept", "/api/v1/realestate/irl/sync", "PUT /api/v1/realestate/irl/{year:int}/{quarter:int}", "/api/v1/realestate/loans/{id:guid}/simulations", "/api/v1/realestate/loans/overview/simulations",
        "/api/v1/realestate/loans/{id:guid}/bank-schedule/preview", "PUT /api/v1/realestate/loans/{id:guid}/bank-schedule",
        "/api/v1/tax/", "PUT /api/v1/dashboard/allocation-target", "/api/v1/scenarios/", "/api/v1/platform/notifications/",
        // Documents: the assistant links one (propose_link_document), never uploads, edits, archives or unlinks it.
        "/api/v1/documents",
    ];

    private static async Task<McpClient> ClientAsync(NidApp app, string preset, PersonId? person = null)
        => await McpHarness.ClientAsync(app, (await TokenRouteTests.CreateAsync(app, person ?? app.AdultA, preset)).Secret);

    private static async Task<(bool Error, string Text)> CallAsync(McpClient client, string tool, object args)
    {
        var result = await client.CallToolAsync(tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!);
        return (result.IsError == true, McpHarness.TextOf(result));
    }

    private static async Task<JsonElement> AnswerAsync(McpClient client, string tool, object args)
    {
        var (error, text) = await CallAsync(client, tool, args);
        Assert.False(error, text);
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<JsonElement> PendingAsync(NidApp app, PersonId person)
        => JsonDocument.Parse(await app.GetRawAsync(person, ProposalRouteTests.Proposals)).RootElement.GetProperty("items");

    private static async Task<HttpResponseMessage> ApproveAsync(NidApp app, PersonId person, Guid proposal)
    {
        var detail = JsonDocument.Parse(await app.GetRawAsync(person, $"{ProposalRouteTests.Proposals}/{proposal}")).RootElement;
        return await app.RawAsync(person, HttpMethod.Post, $"{ProposalRouteTests.Proposals}/{proposal}/approve",
            new { version = detail.GetProperty("proposal").GetProperty("version").GetUInt32() });
    }

    private static async Task<int> PropertyCountAsync(NidApp app)
        => JsonDocument.Parse(await app.GetRawAsync(app.AdultA, PropertyApi.Properties)).RootElement.GetProperty("items").GetArrayLength();

    [Fact]
    public async Task Every_web_write_is_a_tool_or_left_out_on_purpose_and_each_tool_matches_its_route()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()?.HttpMethods ?? [])
                .Where(m => m is "POST" or "PUT" or "PATCH" or "DELETE").Select(m => (Method: m, Endpoint: e)))
            .Where(w => w.Endpoint.RoutePattern.RawText!.StartsWith("/api/v1/"))
            .ToList();

        foreach (var action in RouteActions.All)
        {
            var match = Assert.Single(endpoints, w => w.Method == action.Method && w.Endpoint.RoutePattern.RawText == action.Route);
            Assert.Equal(action.Body, match.Endpoint.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType);
        }
        var uncovered = endpoints
            .Where(w => !RouteActions.All.Any(a => a.Method == w.Method && a.Route == w.Endpoint.RoutePattern.RawText))
            .Select(w => $"{w.Method} {w.Endpoint.RoutePattern.RawText}")
            .Where(w => !NotProposed.Any(left => w.Contains(left, StringComparison.Ordinal)))
            .ToList();
        Assert.Empty(uncovered);

        await using var client = await ClientAsync(app, TokenPresets.Propose);
        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name);
        Assert.All(RouteActions.All, a => Assert.Contains(a.Tool, tools.Keys));
        Assert.Equal(RouteActions.All.Count, RouteActions.All.Select(a => a.Tool).Distinct().Count());
    }

    /// <summary>« À valider » names each action from <c>proposals.action</c> of the web's locale.</summary>
    [Fact]
    public void Every_action_has_its_french_label()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Nid.slnx")))
            root = root.Parent!;
        var labels = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "web", "src", "locales", "fr.proposals.json")))
            .RootElement.GetProperty("proposal").GetProperty("action");
        Assert.Equal(RouteActions.All.Select(a => a.Name).Order(StringComparer.Ordinal), labels.EnumerateObject().Select(l => l.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_property_and_its_loan_are_proposed_then_created_when_the_adult_approves()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var propose = await ClientAsync(app, TokenPresets.Propose);

        var first = await AnswerAsync(propose, "propose_create_property", new { body = PropertyApi.Home(app), sourceLabel = "Acte de vente" });
        Assert.Equal(("proposed", true), (first.GetProperty("outcome").GetString(), first.GetProperty("created").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, first.GetProperty("proposal").GetProperty("targetItemId").ValueKind);
        // Idempotent: the same call is the same proposal; the dry run wrote nothing.
        Assert.False((await AnswerAsync(propose, "propose_create_property", new { body = PropertyApi.Home(app), sourceLabel = "Acte de vente" }))
            .GetProperty("created").GetBoolean());
        Assert.Equal(0, await PropertyCountAsync(app));

        // A proposal without a target is its submitter's alone.
        Assert.Equal(0, (await PendingAsync(app, app.AdultB)).GetArrayLength());
        var pending = Assert.Single((await PendingAsync(app, app.AdultA)).EnumerateArray());
        Assert.Equal("realestate.create_property", pending.GetProperty("kind").GetString());
        var proposalId = pending.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{ProposalRouteTests.Proposals}/{proposalId}")).StatusCode);

        var approved = await ApproveAsync(app, app.AdultA, proposalId);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var property = JsonDocument.Parse(await approved.Content.ReadAsStringAsync()).RootElement.GetProperty("appliedRefs")[0].GetGuid();
        Assert.Equal("Maison", (await PropertyApi.PropertyAsync(app, property)).GetProperty("name").GetString());

        var loanBody = LoanApi.Golden(app);
        loanBody["propertyId"] = property;
        var loan = await AnswerAsync(propose, "propose_create_loan", new { body = loanBody });
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(app, app.AdultA, loan.GetProperty("proposal").GetProperty("id").GetGuid())).StatusCode);
        Assert.Single(JsonDocument.Parse(await app.GetRawAsync(app.AdultA, LoanApi.Loans)).RootElement.GetProperty("items").EnumerateArray());
    }

    /// <summary>Issue #34 §2.5: a cost report's year is the first integer route parameter.</summary>
    [Fact]
    public async Task A_cost_report_is_proposed_with_its_year_and_written_when_approved()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var propose = await ClientAsync(app, TokenPresets.Propose);
        var envelope = await app.NewEnvelopeAsync("Assurance-vie", app.AdultA, Visibility.Shared);
        var envelopes = $"/api/v1/investments/envelopes/{envelope.Value}";
        var holding = JsonDocument.Parse(await (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{envelopes}/holdings", new { name = "Fonds A", kind = "fund" }))
            .Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var body = new { grossReturnPct = 8.0m, totalFeesPct = 2.5m, netReturnPct = 5.5m };

        var (error, text) = await CallAsync(propose, "propose_save_holding_cost_report", new { id = envelope.Value, holdingId = holding, year = "2024", body });
        Assert.True(error);
        Assert.Contains("payload.year: An integer is required.", text);

        var proposed = await AnswerAsync(propose, "propose_save_holding_cost_report", new { id = envelope.Value, holdingId = holding, year = 2024, body });
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(app, app.AdultA, proposed.GetProperty("proposal").GetProperty("id").GetGuid())).StatusCode);
        Assert.Equal(5.5m, JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"{envelopes}/holdings/{holding}/cost-reports/2024")).RootElement
            .GetProperty("netReturnPct").GetDecimal());
    }

    /// <summary>Issue #179: a document already in Nid is linked to an income period, an ended one included, without a pay change.</summary>
    [Fact]
    public async Task A_document_link_is_proposed_without_a_target_item_and_linked_when_approved()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var propose = await ClientAsync(app, TokenPresets.Propose);
        var source = await app.NewIncomeSourceAsync("Salaire", Visibility.Shared, (app.AdultA, 10_000));
        var phase = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/income/sources/{source.Value}")).RootElement
            .GetProperty("phases")[0].GetProperty("id").GetGuid();
        var document = await Documents.DocumentEndpointTests.UploadedAsync(app, app.AdultA, Documents.DocumentEndpointTests.Pdf);
        async Task<JsonElement> LinkedAsync() => JsonDocument.Parse(await app.GetRawAsync(app.AdultA,
            $"{Documents.DocumentEndpointTests.Documents}?targetKind=income.phase&targetId={phase}")).RootElement.GetProperty("items");

        var proposed = await AnswerAsync(propose, "propose_link_document",
            new { id = document, body = new { targetKind = "income.phase", targetId = phase, role = "payslip" } });

        var proposal = proposed.GetProperty("proposal");
        Assert.Equal(("documents.link_document", JsonValueKind.Null), (proposal.GetProperty("kind").GetString(), proposal.GetProperty("targetItemId").ValueKind));
        Assert.Equal(0, (await LinkedAsync()).GetArrayLength());

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(app, app.AdultA, proposal.GetProperty("id").GetGuid())).StatusCode);
        var linked = Assert.Single((await LinkedAsync()).EnumerateArray());
        Assert.Equal(document, linked.GetProperty("id").GetGuid());
        Assert.Equal("payslip", Assert.Single(linked.GetProperty("links").EnumerateArray()).GetProperty("role").GetString());
        Assert.Single(JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/income/sources/{source.Value}")).RootElement.GetProperty("phases").EnumerateArray());
    }

    [Fact]
    public async Task A_refused_field_is_named_by_its_payload_path_and_nothing_is_proposed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var propose = await ClientAsync(app, TokenPresets.Propose);
        var body = PropertyApi.Home(app);
        body["purchasePrice"] = "250 000,00";

        var (error, text) = await CallAsync(propose, "propose_create_property", new { body });

        Assert.True(error);
        Assert.StartsWith("Refused: ", text);
        Assert.Contains("payload.body.purchasePrice:", text);
        Assert.Equal(0, (await PendingAsync(app, app.AdultA)).GetArrayLength());
        Assert.Equal(0, await PropertyCountAsync(app));
    }

    [Fact]
    public async Task Applying_directly_needs_write_direct_and_then_writes_at_once()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using (var propose = await ClientAsync(app, TokenPresets.Propose))
        {
            var (error, text) = await CallAsync(propose, "propose_create_property", new { body = PropertyApi.Home(app), apply = true });
            Assert.True(error);
            Assert.Contains("write:direct", text);
        }

        await using var direct = await ClientAsync(app, TokenPresets.Direct);
        var applied = await AnswerAsync(direct, "propose_create_property", new { body = PropertyApi.Home(app), apply = true });
        Assert.Equal("applied", applied.GetProperty("outcome").GetString());
        Assert.Equal("Maison", (await PropertyApi.PropertyAsync(app, applied.GetProperty("appliedRefs")[0].GetGuid())).GetProperty("name").GetString());
        Assert.Equal(0, (await PendingAsync(app, app.AdultA)).GetArrayLength());
    }

    /// <summary>Savings goals §5: a progress entry's route names its date, an ISO date the tool reads and replays.</summary>
    [Fact]
    public async Task A_goal_progress_entry_is_written_through_its_date_route_parameter()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var goal = await app.NewGoalAsync("Voyage", app.AdultA);
        await using var direct = await ClientAsync(app, TokenPresets.Direct);

        var (error, text) = await CallAsync(direct, "propose_record_goal_progress", new { id = goal.Value, on = "15/10/2025", body = new { amount = "100" }, apply = true });
        Assert.True(error);
        Assert.Contains("payload.on: An ISO date (yyyy-mm-dd) is required.", text);

        var applied = await AnswerAsync(direct, "propose_record_goal_progress", new { id = goal.Value, on = "2025-10-15", body = new { amount = "100" }, apply = true });
        Assert.Equal("applied", applied.GetProperty("outcome").GetString());
        await using var scope = app.Services.CreateAsyncScope();
        var entry = Assert.Single(await scope.ServiceProvider.GetRequiredService<Nid.Goals.Application.IGoalStore>().EntriesOfAsync([goal], default));
        Assert.Equal((new NodaTime.LocalDate(2025, 10, 15), 100m), (entry.On, entry.Amount));
    }

    [Fact]
    public async Task A_change_to_an_item_targets_it_and_another_adults_private_item_is_not_found()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await McpSeed.IncomeSourceAsync(app, app.AdultA);
        var hidden = await app.NewEnvelopeAsync("Privé", app.AdultB, Visibility.Private);
        await using var propose = await ClientAsync(app, TokenPresets.Propose);

        var bonus = await AnswerAsync(propose, "propose_add_one_off_income", new
        {
            id = source, body = new { occurredOn = "2025-06-20", amount = new { amount = "1500.00", currency = "EUR" }, note = "Prime" },
        });
        Assert.Equal(source, bonus.GetProperty("proposal").GetProperty("targetItemId").GetGuid());
        // The other adult reads the shared source, so they see and may decide its proposal.
        Assert.Single((await PendingAsync(app, app.AdultB)).EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(app, app.AdultA, bonus.GetProperty("proposal").GetProperty("id").GetGuid())).StatusCode);
        var detail = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/income/sources/{source}")).RootElement;
        Assert.Contains("1500", detail.GetRawText());

        Assert.Equal((true, "Not found."), await CallAsync(propose, "propose_add_valuation", new
        {
            id = hidden.Value, body = new { asOf = "2025-09-30", amount = new { amount = "1.00", currency = "EUR" } },
        }));
    }
}
