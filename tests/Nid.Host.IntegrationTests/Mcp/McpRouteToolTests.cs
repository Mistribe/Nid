using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Nid.Core.Contracts;
using Nid.Host.Mcp;
using Nid.Host.IntegrationTests.Admin;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Mcp;

/// <summary>Spec 4D3: every web write as a tool, proposed by default and approved by replaying its route.</summary>
public class McpRouteToolTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    /// <summary>The writes no tool proposes, on purpose (<see cref="RouteActions"/>' summary).</summary>
    private static readonly string[] NotProposed =
    [
        "/api/v1/admin/", "/api/v1/platform/imports", "/api/v1/core/proposals", "/api/v1/core/me/", "/api/v1/core/fx-rates/",
        "/api/v1/subscriptions/catalog/sync", "/api/v1/investments/prices/sync", "/price/accept", "/api/v1/realestate/irl/sync", "PUT /api/v1/realestate/irl/{year:int}/{quarter:int}", "/api/v1/realestate/loans/{id:guid}/simulations", "/api/v1/realestate/loans/overview/simulations",
        "/api/v1/realestate/loans/{id:guid}/bank-schedule/preview", "PUT /api/v1/realestate/loans/{id:guid}/bank-schedule",
        "/api/v1/tax/", "PUT /api/v1/dashboard/allocation-target", "PUT /api/v1/investments/fee-references", "/api/v1/scenarios/", "/api/v1/platform/notifications/",
        // Documents: the assistant links one (propose_link_document), never uploads, edits, archives or unlinks it.
        "/api/v1/documents",
        // The household's projection assumptions (spec §7): read in the web app, which is where they are set.
        "/api/v1/core/projection-assumptions",
        // Bank sync §8: connecting a bank needs the person at their bank, in the browser; no tool touches a connection.
        "/api/v1/banking/connections",
        // The sync reads the person's banks with their browser's address: a click, not a proposal.
        "/api/v1/banking/sync",
        // A statement file is dropped in the browser and confirmed after its preview: browser-only.
        "/api/v1/banking/statements",
        // A line linked to a charge: propose_bank_link proposes it, with the line and the charge in its preview.
        "POST /api/v1/banking/transactions/{id:guid}/link",
        // An insurance is set by propose_set_insurance (spec 2026-10-30 §10); removing it is a click in the web app.
        "DELETE /api/v1/subscriptions/{id:guid}/insurance",
        // Childcare spec decision 27: validating, reopening or restarting a statement stays the person's gesture in the browser.
        "/api/v1/childcare/{id:guid}/statements/{month}/validate", "/api/v1/childcare/{id:guid}/statements/{month}/reopen",
        "DELETE /api/v1/childcare/{id:guid}/statements/{month}",
        // A holding's breakdown is proposed by propose_breakdown (from its factsheet, with its document), by ISIN; changing or deleting one is a click.
        "/holdings/{holdingId:guid}/breakdown",
        // #294: who may edit an item is its owners' click in the web app, never a proposal.
        "PUT /api/v1/core/items/{id:guid}/sharing",
    ];

    private static async Task<McpClient> ClientAsync(NidApp app, string preset, PersonId? person = null)
        => await McpHarness.ClientAsync(app, (await TokenApi.CreateAsync(app, person ?? app.AdultA, preset)).Secret);

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
        => JsonDocument.Parse(await app.GetRawAsync(person, ProposalKindHelpers.Proposals)).RootElement.GetProperty("items");

    private static async Task<HttpResponseMessage> ApproveAsync(NidApp app, PersonId person, Guid proposal)
    {
        var detail = JsonDocument.Parse(await app.GetRawAsync(person, $"{ProposalKindHelpers.Proposals}/{proposal}")).RootElement;
        return await app.RawAsync(person, HttpMethod.Post, $"{ProposalKindHelpers.Proposals}/{proposal}/approve",
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
        var tools = (await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).ToDictionary(t => t.Name);
        Assert.All(RouteActions.All, a => Assert.Contains(a.Tool, tools.Keys));
        Assert.Equal(RouteActions.All.Count, RouteActions.All.Select(a => a.Tool).Distinct().Count());
    }

    /// <summary>Net worth projection §7: the read tool answers as its route does, its points trimmed to today, the year ends and the last.</summary>
    [Fact]
    public async Task Get_projection_returns_the_route_answer_at_year_ends()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var client = await ClientAsync(app, TokenPresets.Read);

        var rest = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/projection?scope=me&horizon=5y")).RootElement;
        var mcp = await AnswerAsync(client, "get_projection", new { scope = "me", horizon = "5y" });

        var all = rest.GetProperty("points").EnumerateArray().ToList();
        var kept = all.Where((p, i) => i == 0 || i == all.Count - 1 || p.GetProperty("on").GetString()!.EndsWith("-12-31", StringComparison.Ordinal))
            .Select(p => p.ToString()).ToList();
        Assert.Equal(7, kept.Count); // 2025-10-15, five December 31sts, 2030-10-31
        Assert.Equal(kept, mcp.GetProperty("points").EnumerateArray().Select(p => p.ToString()));
        Assert.Equal(rest.EnumerateObject().Where(p => p.Name != "points").Select(p => p.ToString()),
            mcp.EnumerateObject().Where(p => p.Name != "points").Select(p => p.ToString()));

        var (error, text) = await CallAsync(client, "get_projection", new { horizon = "41y" });
        Assert.True(error);
        Assert.NotEmpty(text);
    }

    private static IEnumerable<string> YearEndPoints(JsonElement view)
    {
        var all = view.GetProperty("points").EnumerateArray().ToList();
        return all.Where((p, i) => i == 0 || i == all.Count - 1 || p.GetProperty("on").GetString()!.EndsWith("-12-31", StringComparison.Ordinal))
            .Select(p => p.ToString());
    }

    private static object ProjectionDocument(params object[] events)
        => new { version = 1, horizon = "5y", assumptions = new { }, events };

    private static object RetirementIncome()
        => new { id = Guid.NewGuid(), kind = "income_change", label = "Retraite", start = new { on = "2028-01-01" }, monthlyAmount = "-1800" };

    private static async Task<Guid> SaveAsync(NidApp app, string url, object document)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, url, new
        {
            name = "Test", visibility = "shared", scope = "household", owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } }, document,
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>Net worth projection §7: a saved projection scenario read by id answers as its route does, both curves at year ends; a purchase's id is « Not found. ».</summary>
    [Fact]
    public async Task Get_projection_with_a_scenario_returns_both_curves()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var client = await ClientAsync(app, TokenPresets.Read);
        var id = await SaveAsync(app, "/api/v1/scenarios/projection", ProjectionDocument(RetirementIncome()));

        var rest = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/scenarios/projection/{id}")).RootElement.GetProperty("result");
        // The scenario's stored horizon (5y) governs, whatever horizon is sent.
        var mcp = await AnswerAsync(client, "get_projection", new { scenarioId = id, horizon = "20y" });

        Assert.Equal(YearEndPoints(rest.GetProperty("base")), YearEndPoints(mcp.GetProperty("base")));
        Assert.Equal(YearEndPoints(rest.GetProperty("scenario")), YearEndPoints(mcp.GetProperty("scenario")));
        Assert.Equal(7, mcp.GetProperty("scenario").GetProperty("points").GetArrayLength());
        Assert.Equal(rest.GetProperty("gapNominal").GetString(), mcp.GetProperty("gapNominal").GetString());
        // The gaps trimmed to the same year ends as the points, each the route's own.
        var gaps = mcp.GetProperty("gaps").EnumerateArray().ToList();
        Assert.Equal(7, gaps.Count);
        var restGaps = rest.GetProperty("gaps").EnumerateArray().ToDictionary(g => g.GetProperty("on").GetString()!, g => g.GetProperty("nominal").GetString());
        Assert.All(gaps, g => Assert.Equal(restGaps[g.GetProperty("on").GetString()!], g.GetProperty("nominal").GetString()));
        Assert.Equal(rest.GetProperty("gapNominal").GetString(), gaps[^1].GetProperty("nominal").GetString());

        var (error, text) = await CallAsync(client, "get_projection", new { scenarioId = Guid.NewGuid() });
        Assert.True(error);
        Assert.Equal("Not found.", text);
    }

    /// <summary>Net worth projection §7: simulating answers as the simulate route does, trimmed; an invalid event is a tool error naming its field.</summary>
    [Fact]
    public async Task Simulate_projection_returns_the_simulate_route_answer_trimmed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var client = await ClientAsync(app, TokenPresets.Read);
        var ev = RetirementIncome();

        var rest = JsonDocument.Parse(await (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/scenarios/projection/simulate",
            new { scope = "household", document = ProjectionDocument(ev) })).Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        var mcp = await AnswerAsync(client, "simulate_projection", new { horizon = "5y", events = new[] { ev } });

        Assert.Equal(YearEndPoints(rest.GetProperty("base")), YearEndPoints(mcp.GetProperty("base")));
        Assert.Equal(YearEndPoints(rest.GetProperty("scenario")), YearEndPoints(mcp.GetProperty("scenario")));
        Assert.Equal(7, mcp.GetProperty("base").GetProperty("points").GetArrayLength());
        Assert.Equal(rest.GetProperty("gapNominal").GetString(), mcp.GetProperty("gapNominal").GetString());
        Assert.Equal(rest.GetProperty("gapReal").GetString(), mcp.GetProperty("gapReal").GetString());
    }

    [Fact]
    public async Task Simulate_projection_invalid_event_is_a_tool_error()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var client = await ClientAsync(app, TokenPresets.Read);

        var (error, text) = await CallAsync(client, "simulate_projection", new
        {
            events = new[] { new { kind = "income_change", start = new { on = "2028-01-01" }, monthlyAmount = "abc" } },
        });
        Assert.True(error);
        Assert.Contains("events[0].monthlyAmount", text);

        var (badHorizon, horizonText) = await CallAsync(client, "simulate_projection", new { horizon = "soon", events = Array.Empty<object>() });
        Assert.True(badHorizon);
        Assert.Contains("horizon", horizonText);

        var (badScope, scopeText) = await CallAsync(client, "simulate_projection", new { scope = "everyone", events = Array.Empty<object>() });
        Assert.True(badScope);
        Assert.Contains("'everyone' is not a scope", scopeText);
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
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{ProposalKindHelpers.Proposals}/{proposalId}")).StatusCode);

        var approved = await ApproveAsync(app, app.AdultA, proposalId);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var property = JsonDocument.Parse(await approved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("appliedRefs")[0].GetGuid();
        Assert.Equal("Maison", (await PropertyApi.PropertyAsync(app, property)).GetProperty("name").GetString());

        var loanBody = LoanApi.Golden(app);
        loanBody["propertyId"] = property;
        var loan = await AnswerAsync(propose, "propose_create_loan", new { body = loanBody });
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(app, app.AdultA, loan.GetProperty("proposal").GetProperty("id").GetGuid())).StatusCode);
        Assert.Single(JsonDocument.Parse(await app.GetRawAsync(app.AdultA, LoanApi.Loans)).RootElement.GetProperty("items").EnumerateArray());
    }

    /// <summary>Issue #284: a person is no item, so the proposal has no target; once approved, the projection to their retirement answers.</summary>
    [Fact]
    public async Task A_birth_date_is_proposed_and_the_projection_to_retirement_answers_once_approved()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var propose = await ClientAsync(app, TokenPresets.Propose);
        var id = app.AdultA.Value;
        var horizon = $"retire:{id}";

        var (refused, why) = await CallAsync(propose, "get_projection", new { horizon });
        Assert.True(refused);
        Assert.Contains("projection.no_birth_date", why);

        var proposed = await AnswerAsync(propose, "propose_set_retirement", new { id, body = new { birthDate = "1984-05-20", retirementAge = 62 } });
        Assert.Equal(JsonValueKind.Null, proposed.GetProperty("proposal").GetProperty("targetItemId").ValueKind);
        var (stillRefused, _) = await CallAsync(propose, "get_projection", new { horizon }); // the dry run wrote nothing
        Assert.True(stillRefused);

        var pending = Assert.Single((await PendingAsync(app, app.AdultA)).EnumerateArray());
        Assert.Equal("core.set_retirement", pending.GetProperty("kind").GetString());
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(app, app.AdultA, pending.GetProperty("id").GetGuid())).StatusCode);

        var person = (await AnswerAsync(propose, "list_persons", new { })).EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == id);
        Assert.Equal(("1984-05-20", 62), (person.GetProperty("birthDate").GetString(), person.GetProperty("retirementAge").GetInt32()));
        await AnswerAsync(propose, "get_projection", new { horizon });

        var (invalid, reason) = await CallAsync(propose, "propose_set_retirement", new { id, body = new { birthDate = "1984-05-20", retirementAge = 40 } });
        Assert.True(invalid);
        Assert.Contains("payload.body.retirementAge", reason);
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
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("id").GetGuid();
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
        var document = await DocumentApi.UploadedAsync(app, app.AdultA, DocumentApi.Pdf);
        async Task<JsonElement> LinkedAsync() => JsonDocument.Parse(await app.GetRawAsync(app.AdultA,
            $"{DocumentApi.Documents}?targetKind=income.phase&targetId={phase}")).RootElement.GetProperty("items");

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

    /// <summary>Bank sync §8: associating a bank account is a web write like the others; its <c>{id}</c> is a bank account, no item.</summary>
    [Fact]
    public async Task A_bank_account_update_is_proposed_without_a_target_and_applied_when_approved()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code", BankingApi.Account("uid", "hash", null, "Courant"));
        var account = (await BankingApi.AccountsAsync(app, app.AdultA))[0].GetProperty("id").GetGuid();
        var envelope = await app.NewEnvelopeAsync("Courant", app.AdultA, Visibility.Private);
        async Task<JsonValueKind> AssociatedAsync() => (await BankingApi.AccountsAsync(app, app.AdultA))[0].GetProperty("envelopeId").ValueKind;
        await using var propose = await ClientAsync(app, TokenPresets.Propose);

        var (error, _) = await CallAsync(propose, "propose_bank_account_update", new { id = Guid.NewGuid(), body = new { ignored = true } });
        Assert.True(error); // the dry run meets the route's « not found »

        var proposed = await AnswerAsync(propose, "propose_bank_account_update", new { id = account, body = new { envelopeId = envelope.Value } });
        var proposal = proposed.GetProperty("proposal");
        Assert.Equal(("banking.bank_account_update", JsonValueKind.Null), (proposal.GetProperty("kind").GetString(), proposal.GetProperty("targetItemId").ValueKind));
        Assert.Equal(JsonValueKind.Null, await AssociatedAsync());

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(app, app.AdultA, proposal.GetProperty("id").GetGuid())).StatusCode);
        Assert.Equal(envelope.Value, (await BankingApi.AccountsAsync(app, app.AdultA))[0].GetProperty("envelopeId").GetGuid());
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
        var entry = Assert.Single(await scope.ServiceProvider.GetRequiredService<Nid.Goals.Application.IGoalStore>().EntriesOfAsync([goal], TestContext.Current.CancellationToken));
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

    /// <summary>Issue #208: the contract terms are set through the edit tool, approved, then read back by the web's route.</summary>
    [Fact]
    public async Task Contract_terms_are_proposed_through_edit_subscription_and_set_when_approved()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);
        await using var propose = await ClientAsync(app, TokenPresets.Propose);

        var proposed = await AnswerAsync(propose, "propose_edit_subscription", new
        {
            id = id.Value,
            body = new { anniversaryOn = "2026-03-01", notice = new { count = 2, unit = "month" }, cancellableFrom = "2026-03-01" },
        });
        Assert.Equal(id.Value, proposed.GetProperty("proposal").GetProperty("targetItemId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(app, app.AdultA, proposed.GetProperty("proposal").GetProperty("id").GetGuid())).StatusCode);

        var terms = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/subscriptions/{id.Value}")).RootElement.GetProperty("contractTerms");
        Assert.Equal("2026-03-01", terms.GetProperty("anniversaryOn").GetString());
        Assert.Equal((2, "month"), (terms.GetProperty("notice").GetProperty("count").GetInt32(), terms.GetProperty("notice").GetProperty("unit").GetString()));
    }

    /// <summary>Issue #208: the insurance is proposed through set_insurance, approved, then read back by the web's route.</summary>
    [Fact]
    public async Task Insurance_is_proposed_through_set_insurance_and_set_when_approved()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);
        var property = await app.NewPropertyAsync("Maison", Visibility.Shared, (app.AdultA, 10_000));
        await using var propose = await ClientAsync(app, TokenPresets.Propose);

        var proposed = await AnswerAsync(propose, "propose_set_insurance", new
        {
            id = id.Value,
            body = new
            {
                kind = "home", propertyId = property.Value, policyNumber = "HX-123",
                guarantees = new[] { new { label = "Fire", ceiling = "100 000 EUR", deductible = "150 EUR" } },
            },
        });
        Assert.Equal(id.Value, proposed.GetProperty("proposal").GetProperty("targetItemId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(app, app.AdultA, proposed.GetProperty("proposal").GetProperty("id").GetGuid())).StatusCode);

        var insurance = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/subscriptions/{id.Value}")).RootElement.GetProperty("insurance");
        Assert.Equal("home", insurance.GetProperty("kind").GetString());
        Assert.Equal(property.Value, insurance.GetProperty("property").GetProperty("id").GetGuid());
        Assert.Equal("Fire", insurance.GetProperty("guarantees")[0].GetProperty("label").GetString());
    }

    /// <summary>A co-owner who cannot read the property proposes with keepProperty: once approved, the stored link is still there.</summary>
    [Fact]
    public async Task A_set_insurance_proposal_with_keep_property_keeps_a_stored_link_once_approved()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewOwnedSubscriptionAsync("Home insurance", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));
        var property = await app.NewPropertyAsync("Private house", Visibility.Private, (app.AdultA, 10_000));
        (await app.RawAsync(app.AdultA, HttpMethod.Put, $"/api/v1/subscriptions/{id.Value}/insurance",
            new { kind = "home", propertyId = property.Value, guarantees = new[] { new { label = "Fire" } } })).EnsureSuccessStatusCode();
        await using var propose = await ClientAsync(app, TokenPresets.Propose, app.AdultB);

        var proposed = await AnswerAsync(propose, "propose_set_insurance", new
        {
            id = id.Value,
            body = new { kind = "home", keepProperty = true, policyNumber = "HX-999", guarantees = new[] { new { label = "Fire" }, new { label = "Flood" } } },
        });
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(app, app.AdultB, proposed.GetProperty("proposal").GetProperty("id").GetGuid())).StatusCode);

        var insurance = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/subscriptions/{id.Value}")).RootElement.GetProperty("insurance");
        Assert.Equal(property.Value, insurance.GetProperty("property").GetProperty("id").GetGuid());
        Assert.Equal("HX-999", insurance.GetProperty("policyNumber").GetString());
        Assert.Equal(2, insurance.GetProperty("guarantees").GetArrayLength());
    }
}
