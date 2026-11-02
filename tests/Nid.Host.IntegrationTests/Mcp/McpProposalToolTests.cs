using System.Net;
using System.Text.Json;
using ModelContextProtocol.Client;
using Nid.Core.Contracts;
using Nid.Documents.Contracts;
using Nid.Host.IntegrationTests.Admin;
using Nid.TestSupport;
using static Nid.TestSupport.DocumentApi;

namespace Nid.Host.IntegrationTests.Mcp;

/// <summary>Issue #291: `list_proposals` and `get_proposal`, the views of the web routes, the same rights, nothing to decide with.</summary>
public class McpProposalToolTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Proposals = ProposalKindHelpers.Proposals;

    private static async Task<McpClient> ClientAsync(NidApp app, PersonId person, string preset = TokenPresets.Read)
        => await McpHarness.ClientAsync(app, (await TokenApi.CreateAsync(app, person, preset)).Secret);

    private static async Task<(bool Error, string Text)> CallAsync(McpClient client, string tool, object args)
    {
        var result = await client.CallToolAsync(tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!);
        return (result.IsError == true, McpHarness.TextOf(result));
    }

    private static async Task<JsonElement> ReadAsync(McpClient client, string tool, object args)
    {
        var (error, text) = await CallAsync(client, tool, args);
        Assert.False(error, text);
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<Guid[]> IdsAsync(McpClient client, object args)
        => (await ReadAsync(client, "list_proposals", args)).GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToArray();

    private static Task<JsonElement> ValuationAsync(NidApp app, Guid envelope, string asOf, string amount = "23500.00")
        => ProposalKindHelpers.SubmitAsync(app, new
        {
            kind = "investments.record_valuation", targetItemId = envelope,
            payload = new { asOf, amount = new { amount, currency = "EUR" } }, sourceLabel = "Relevé PEA",
        });

    private static async Task<JsonElement> RejectAsync(NidApp app, JsonElement proposal, string note)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Proposals}/{proposal.GetProperty("id").GetGuid()}/reject",
            new { version = proposal.GetProperty("version").GetUInt32(), note });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ProposalKindHelpers.JsonOf(response);
    }

    private static async Task<JsonElement> ApproveEditedAsync(NidApp app, JsonElement proposal, string asOf, string amount)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Proposals}/{proposal.GetProperty("id").GetGuid()}/approve", new
        {
            version = proposal.GetProperty("version").GetUInt32(),
            editedPayload = new { asOf, amount = new { amount, currency = "EUR" } },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ProposalKindHelpers.JsonOf(response);
    }

    [Fact]
    public async Task List_proposals_is_pending_by_default_filters_on_the_four_statuses_and_refuses_another()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        var superseded = await ValuationAsync(app, envelope, "2025-09-30");
        var pending = await ValuationAsync(app, envelope, "2025-09-30", "23600.00");
        var rejected = await RejectAsync(app, await ValuationAsync(app, envelope, "2025-08-31"), "Mauvais mois");
        var approved = await ApproveEditedAsync(app, await ValuationAsync(app, envelope, "2025-07-31"), "2025-07-31", "23400.00");
        await using var client = await ClientAsync(app, app.AdultA);

        Assert.Equal([pending.GetProperty("id").GetGuid()], await IdsAsync(client, new { }));
        Assert.Equal([pending.GetProperty("id").GetGuid()], await IdsAsync(client, new { status = "pending" }));
        Assert.Equal([approved.GetProperty("id").GetGuid()], await IdsAsync(client, new { status = "approved" }));
        Assert.Equal([rejected.GetProperty("id").GetGuid()], await IdsAsync(client, new { status = "rejected" }));
        Assert.Equal([superseded.GetProperty("id").GetGuid()], await IdsAsync(client, new { status = "superseded" }));
        Assert.Equal(
            JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"{Proposals}?status=rejected")).RootElement.ToString(),
            (await ReadAsync(client, "list_proposals", new { status = "rejected" })).ToString());

        Assert.Equal((true, "Refused: request.invalid."), await CallAsync(client, "list_proposals", new { status = "maybe" }));
    }

    [Fact]
    public async Task Get_proposal_gives_the_payloads_the_preview_and_the_decision_and_a_token_never_decides()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        var waiting = await ValuationAsync(app, envelope, "2025-09-30");
        var refused = await RejectAsync(app, await ValuationAsync(app, envelope, "2025-08-31"), "Mauvais mois");
        var edited = await ApproveEditedAsync(app, await ValuationAsync(app, envelope, "2025-07-31"), "2025-07-31", "23400.00");
        await using var client = await ClientAsync(app, app.AdultA);

        var id = waiting.GetProperty("id").GetGuid();
        var web = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"{Proposals}/{id}")).RootElement;
        var pending = await ReadAsync(client, "get_proposal", new { id });
        Assert.True(web.GetProperty("canDecide").GetBoolean());
        Assert.False(pending.GetProperty("canDecide").GetBoolean());
        Assert.Equal(web.GetProperty("proposal").ToString(), pending.GetProperty("proposal").ToString());
        Assert.Equal("23500.00", pending.GetProperty("proposal").GetProperty("payload").GetProperty("amount").GetProperty("amount").GetString());
        Assert.Equal(web.GetProperty("preview").ToString(), pending.GetProperty("preview").ToString());
        Assert.NotEqual(JsonValueKind.Null, pending.GetProperty("preview").ValueKind);

        var rejected = (await ReadAsync(client, "get_proposal", new { id = refused.GetProperty("id").GetGuid() })).GetProperty("proposal");
        Assert.Equal(("rejected", "Mauvais mois"), (rejected.GetProperty("status").GetString(), rejected.GetProperty("decisionNote").GetString()));

        var approved = (await ReadAsync(client, "get_proposal", new { id = edited.GetProperty("id").GetGuid() })).GetProperty("proposal");
        Assert.Equal("23400.00", approved.GetProperty("editedPayload").GetProperty("amount").GetProperty("amount").GetString());
    }

    [Fact]
    public async Task A_proposal_the_person_cannot_read_answers_not_found_and_one_without_a_target_reaches_its_submitter_alone()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await McpSeed.IncomeSourceAsync(app, app.AdultB, "private");
        await using var theirs = await ClientAsync(app, app.AdultB, TokenPresets.Propose);
        var (paid, payslip) = await CallAsync(theirs, "propose_payslip",
            new { incomeSourceId = source, paidOn = "2025-09-28", net = new { amount = "2400.00", currency = "EUR" } });
        Assert.False(paid, payslip);
        var targeted = JsonDocument.Parse(payslip).RootElement.GetProperty("proposal").GetProperty("id").GetGuid();
        await using var mine = await ClientAsync(app, app.AdultA, TokenPresets.Propose);
        var (created, property) = await CallAsync(mine, "propose_create_property", new { body = PropertyApi.Home(app) });
        Assert.False(created, property);
        var untargeted = JsonDocument.Parse(property).RootElement.GetProperty("proposal").GetProperty("id").GetGuid();

        Assert.Equal((true, "Not found."), await CallAsync(mine, "get_proposal", new { id = targeted }));
        Assert.Equal((true, "Not found."), await CallAsync(theirs, "get_proposal", new { id = untargeted }));
        Assert.Equal((true, "Not found."), await CallAsync(mine, "get_proposal", new { id = Guid.NewGuid() }));
        Assert.Equal([targeted], await IdsAsync(theirs, new { }));

        Assert.Equal(untargeted, (await ReadAsync(mine, "get_proposal", new { id = untargeted })).GetProperty("proposal").GetProperty("id").GetGuid());
        Assert.Equal([untargeted], await IdsAsync(mine, new { }));
    }

    [Fact]
    public async Task A_token_deletes_its_persons_own_pending_proposal_and_nothing_else()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.JointEnvelopeAsync(app);
        await using var mine = await ClientAsync(app, app.AdultA, TokenPresets.Propose);
        await using var theirs = await ClientAsync(app, app.AdultB, TokenPresets.Propose);
        await using var reader = await ClientAsync(app, app.AdultA);
        async Task<JsonElement> ProposedAsync(string asOf)
        {
            var (error, text) = await CallAsync(mine, "propose_statement", new { envelopeId = envelope, asOf, value = new { amount = "23500.00", currency = "EUR" } });
            Assert.False(error, text);
            return JsonDocument.Parse(text).RootElement.GetProperty("proposal");
        }
        static object Args(JsonElement p) => new { id = p.GetProperty("id").GetGuid(), version = p.GetProperty("version").GetUInt32() };

        var duplicate = await ProposedAsync("2025-09-30");
        Assert.StartsWith("Refused: deleting a proposal needs the 'write' scope", (await CallAsync(reader, "delete_proposal", Args(duplicate))).Text);
        Assert.StartsWith("Refused: core.proposal_not_submitter.", (await CallAsync(theirs, "delete_proposal", Args(duplicate))).Text);
        Assert.StartsWith("Refused: core.proposal_conflict.", (await CallAsync(mine, "delete_proposal", new { id = duplicate.GetProperty("id").GetGuid(), version = 1u })).Text);

        Assert.Equal((false, "Deleted."), await CallAsync(mine, "delete_proposal", Args(duplicate)));
        Assert.Equal((true, "Not found."), await CallAsync(mine, "get_proposal", new { id = duplicate.GetProperty("id").GetGuid() }));

        var browser = await ValuationAsync(app, envelope, "2025-08-31");
        Assert.StartsWith("Refused: core.proposal_not_submitter.", (await CallAsync(mine, "delete_proposal", Args(browser))).Text);

        var decided = await RejectAsync(app, await ProposedAsync("2025-07-31"), "Non");
        Assert.StartsWith("Refused: core.proposal_decided.", (await CallAsync(mine, "delete_proposal", Args(decided))).Text);
    }

    // The document unlinked, the other adult no longer reads it: both tools hide it, as the web does.
    [Fact]
    public async Task A_document_the_person_cannot_read_stays_hidden_in_both_tools()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.JointEnvelopeAsync(app);
        var document = await UploadedAsync(app, app.AdultA, Pdf);
        await using var propose = await ClientAsync(app, app.AdultA, TokenPresets.Propose);
        var (failed, answer) = await CallAsync(propose, "propose_statement", new
        {
            envelopeId = envelope, asOf = "2025-09-30", value = new { amount = "23500.00", currency = "EUR" }, documentId = document,
        });
        Assert.False(failed, answer);
        var id = JsonDocument.Parse(answer).RootElement.GetProperty("proposal").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK,
            (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{DocumentApi.Documents}/{document}/links/{TargetKinds.CoreItem}/{envelope}")).StatusCode);
        await using var other = await ClientAsync(app, app.AdultB);

        var (_, list) = await CallAsync(other, "list_proposals", new { });
        var (_, detail) = await CallAsync(other, "get_proposal", new { id });
        Assert.Contains(id.ToString(), list);
        Assert.Contains(id.ToString(), detail);
        Assert.DoesNotContain(document.ToString(), list);
        Assert.DoesNotContain(document.ToString(), detail);

        // The reader of the document still gets it.
        Assert.Equal(document, (await ReadAsync(propose, "get_proposal", new { id })).GetProperty("proposal").GetProperty("documentId").GetGuid());
        Assert.Contains(document.ToString(), (await CallAsync(propose, "list_proposals", new { })).Text);
    }
}
