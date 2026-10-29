using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;
using Nid.Core.Contracts;
using Nid.Documents.Contracts;
using Nid.Host.IntegrationTests.Admin;
using Nid.TestSupport;
using static Nid.TestSupport.DocumentApi;

namespace Nid.Host.IntegrationTests.Mcp;

/// <summary>Tranches 2–3 design §4.1–4.2: the hand-written `propose_*` tools cite a document, link it to their element, hide it from non-readers.</summary>
public class McpDocumentProposalTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Root = DocumentApi.Documents;

    private static async Task<McpClient> ClientAsync(NidApp app, string preset = TokenPresets.Propose)
        => await McpHarness.ClientAsync(app, (await TokenApi.CreateAsync(app, app.AdultA, preset)).Secret);

    private static async Task<(bool Error, string Text)> CallAsync(McpClient client, string tool, object args)
    {
        var result = await client.CallToolAsync(tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!);
        return (result.IsError == true, McpHarness.TextOf(result));
    }

    /// <summary>The whole answer of a call that proposed.</summary>
    private static async Task<JsonElement> AnswerAsync(McpClient client, string tool, object args)
    {
        var (error, text) = await CallAsync(client, tool, args);
        Assert.False(error, text);
        var answer = JsonDocument.Parse(text).RootElement;
        Assert.Equal("proposed", answer.GetProperty("outcome").GetString());
        return answer;
    }

    private static async Task<JsonElement> ProposedAsync(McpClient client, string tool, object args)
        => (await AnswerAsync(client, tool, args)).GetProperty("proposal");

    private static object Statement(Guid envelope, Guid? documentId) => new
    {
        envelopeId = envelope, asOf = "2025-09-30", value = new { amount = "23500.00", currency = "EUR" },
        movements = new[] { new { on = "2025-09-05", kind = "contribution", amount = "150.00", label = "Versement", @ref = "R-1" } },
        documentId,
    };

    private static async Task<JsonElement[]> LinksAsync(NidApp app, PersonId reader, Guid document)
        => JsonDocument.Parse(await app.GetRawAsync(reader, $"{Root}/{document}")).RootElement
            .GetProperty("document").GetProperty("links").EnumerateArray().ToArray();

    private static async Task<JsonElement[]> ProposalsAsync(NidApp app, PersonId reader)
        => JsonDocument.Parse(await app.GetRawAsync(reader, ProposalKindHelpers.Proposals)).RootElement.GetProperty("items").EnumerateArray().ToArray();

    private static Guid? DocumentOf(JsonElement proposal)
        => proposal.GetProperty("documentId").ValueKind == JsonValueKind.Null ? null : proposal.GetProperty("documentId").GetGuid();

    /// <summary>A shared envelope both adults own, so either may decide.</summary>
    private static async Task<Guid> JointEnvelopeAsync(NidApp app)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/envelopes", new
        {
            displayName = "PEA commun", kind = "pea", currency = "EUR", visibility = "shared",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 5_000 }, new { personId = app.AdultB.Value, shareBp = 5_000 } },
            institution = "Banque", valuationCadence = "monthly",
            opening = new { occurredOn = "2025-01-31", amount = new { amount = "20000.00", currency = "EUR" } },
            firstValuation = new { asOf = "2025-01-31", amount = new { amount = "21000.00", currency = "EUR" } },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    // Review Focus 2.
    [Fact]
    public async Task A_statement_on_a_shared_envelope_links_its_inbox_document_which_the_decider_reads_and_approval_stamps()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await JointEnvelopeAsync(app);
        var document = await UploadedAsync(app, app.AdultA, Pdf, "fileName=releve.pdf");
        await using var client = await ClientAsync(app);

        var answer = await AnswerAsync(client, "propose_statement", Statement(envelope, document));
        var proposal = answer.GetProperty("proposal");

        Assert.True(answer.GetProperty("sourceLinked").GetBoolean());
        Assert.Equal(document, DocumentOf(proposal));
        var link = Assert.Single(await LinksAsync(app, app.AdultA, document));
        Assert.Equal((TargetKinds.CoreItem, envelope, DocumentRoles.Statement),
            (link.GetProperty("targetKind").GetString(), link.GetProperty("targetId").GetGuid(), link.GetProperty("role").GetString()));

        var seen = Assert.Single(await ProposalsAsync(app, app.AdultB));
        Assert.Equal(document, DocumentOf(seen));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Root}/{document}/content")).StatusCode);

        var approved = await app.RawAsync(app.AdultB, HttpMethod.Post, $"{ProposalKindHelpers.Proposals}/{seen.GetProperty("id").GetGuid()}/approve",
            new { version = seen.GetProperty("version").GetUInt32() });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var refs = JsonDocument.Parse(await approved.Content.ReadAsStringAsync()).RootElement.GetProperty("appliedRefs")
            .EnumerateArray().Select(r => r.GetGuid()).ToList();
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.Equal(document, Assert.Single(await core.Valuations.Where(v => refs.Contains(v.Id)).Select(v => v.DocumentId).ToListAsync()));
        Assert.Equal(document, Assert.Single(await core.Flows.Where(f => refs.Contains(f.Id)).Select(f => f.DocumentId).ToListAsync()));
    }

    [Fact]
    public async Task A_payslip_on_a_private_source_links_its_document_as_a_payslip_read_by_its_owner_alone()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await McpSeed.IncomeSourceAsync(app, app.AdultA, "private");
        var document = await UploadedAsync(app, app.AdultA, Pdf, "fileName=bulletin.pdf");
        await using var client = await ClientAsync(app);

        var proposal = await ProposedAsync(client, "propose_payslip",
            new { incomeSourceId = source, paidOn = "2025-09-28", net = new { amount = "2400.00", currency = "EUR" }, documentId = document });

        Assert.Equal(document, DocumentOf(proposal));
        var link = Assert.Single(await LinksAsync(app, app.AdultA, document));
        Assert.Equal((source, DocumentRoles.Payslip), (link.GetProperty("targetId").GetGuid(), link.GetProperty("role").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Root}/{document}")).StatusCode);
    }

    // Review Focus 1.
    [Fact]
    public async Task Another_adults_private_document_answers_as_a_missing_one_and_nothing_is_proposed_or_linked()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await JointEnvelopeAsync(app);
        var theirs = await UploadedAsync(app, app.AdultB, Pdf);
        await using var propose = await ClientAsync(app);
        await using var direct = await ClientAsync(app, TokenPresets.Direct);

        var hidden = await CallAsync(propose, "propose_statement", Statement(envelope, theirs));
        var missing = await CallAsync(propose, "propose_statement", Statement(envelope, Guid.NewGuid()));
        var applied = await CallAsync(direct, "propose_statement", new { envelopeId = envelope, asOf = "2025-09-30", documentId = theirs, apply = true });

        Assert.Equal((true, "Not found."), hidden);
        Assert.Equal(hidden, missing);
        Assert.Equal(hidden, applied);
        Assert.Empty(await ProposalsAsync(app, app.AdultA));
        Assert.Empty(await LinksAsync(app, app.AdultB, theirs));
    }

    /// <summary>
    /// The link refused while the proposal goes on: B's document, read by A through B's shared subscription that A only reads, so A may not
    /// edit it. A proposes on A's own envelope: no link, the proposal stored with the document, which both adults read.
    /// </summary>
    [Fact]
    public async Task A_refused_link_leaves_the_proposal_stored_with_its_document_and_no_link()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        var box = await app.NewSubscriptionAsync("Box", app.AdultB, Visibility.Shared);
        var document = await UploadedAsync(app, app.AdultB, Pdf, $"fileName=facture.pdf&targetKind=core.item&targetId={box.Value}&role=invoice");
        await using var client = await ClientAsync(app);

        var answer = await AnswerAsync(client, "propose_statement", Statement(envelope, document));

        Assert.False(answer.GetProperty("sourceLinked").GetBoolean()); // so the assistant can say the source was not attached
        Assert.Equal(document, DocumentOf(answer.GetProperty("proposal")));
        Assert.Equal(box.Value, Assert.Single(await LinksAsync(app, app.AdultB, document)).GetProperty("targetId").GetGuid());
        Assert.Equal(document, DocumentOf(Assert.Single(await ProposalsAsync(app, app.AdultA))));
        Assert.Equal(document, DocumentOf(Assert.Single(await ProposalsAsync(app, app.AdultB))));

        // No document given: nothing to say about a source.
        Assert.False((await AnswerAsync(client, "propose_statement", Statement(envelope, null))).TryGetProperty("sourceLinked", out _));
    }

    [Fact]
    public async Task A_direct_write_says_whether_its_source_was_linked()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        var document = await UploadedAsync(app, app.AdultA, Pdf);
        await using var direct = await ClientAsync(app, TokenPresets.Direct);

        var (error, text) = await CallAsync(direct, "propose_statement", new { envelopeId = envelope, asOf = "2025-09-30",
            value = new { amount = "23500.00", currency = "EUR" }, documentId = document, apply = true });

        Assert.False(error, text);
        var answer = JsonDocument.Parse(text).RootElement;
        Assert.Equal(("applied", true), (answer.GetProperty("outcome").GetString(), answer.GetProperty("sourceLinked").GetBoolean()));
        Assert.Equal(envelope, Assert.Single(await LinksAsync(app, app.AdultA, document)).GetProperty("targetId").GetGuid());
    }

    // Review Focus 2: the document unlinked, the other adult no longer reads it, and every view they get hides it.
    [Fact]
    public async Task A_proposals_document_is_hidden_from_an_adult_who_cannot_read_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await JointEnvelopeAsync(app);
        var document = await UploadedAsync(app, app.AdultA, Pdf);
        await using (var client = await ClientAsync(app))
            await ProposedAsync(client, "propose_statement", Statement(envelope, document));
        Assert.Equal(HttpStatusCode.OK,
            (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Root}/{document}/links/{TargetKinds.CoreItem}/{envelope}")).StatusCode);

        var mine = Assert.Single(await ProposalsAsync(app, app.AdultA));
        var theirs = Assert.Single(await ProposalsAsync(app, app.AdultB));
        Assert.Equal(document, DocumentOf(mine));
        Assert.Null(DocumentOf(theirs));
        var id = theirs.GetProperty("id").GetGuid();
        var detail = JsonDocument.Parse(await app.GetRawAsync(app.AdultB, $"{ProposalKindHelpers.Proposals}/{id}")).RootElement;
        Assert.Null(DocumentOf(detail.GetProperty("proposal")));
        Assert.Equal(document, DocumentOf(JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"{ProposalKindHelpers.Proposals}/{id}")).RootElement.GetProperty("proposal")));

        var rejected = await app.RawAsync(app.AdultB, HttpMethod.Post, $"{ProposalKindHelpers.Proposals}/{id}/reject", new { version = theirs.GetProperty("version").GetUInt32() });
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Null(DocumentOf(JsonDocument.Parse(await rejected.Content.ReadAsStringAsync()).RootElement));
        Assert.DoesNotContain(document.ToString(), await app.GetRawAsync(app.AdultB, ProposalKindHelpers.Proposals + "?status=rejected"));
    }

    // The decider's own answers hide the document too.
    [Fact]
    public async Task Approving_a_proposal_answers_the_adult_who_cannot_read_its_document_without_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await JointEnvelopeAsync(app);
        var document = await UploadedAsync(app, app.AdultA, Pdf);
        await using (var client = await ClientAsync(app))
            await ProposedAsync(client, "propose_statement", Statement(envelope, document));
        await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Root}/{document}/links/{TargetKinds.CoreItem}/{envelope}");
        var theirs = Assert.Single(await ProposalsAsync(app, app.AdultB));

        var approved = await app.RawAsync(app.AdultB, HttpMethod.Post, $"{ProposalKindHelpers.Proposals}/{theirs.GetProperty("id").GetGuid()}/approve",
            new { version = theirs.GetProperty("version").GetUInt32() });

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Null(DocumentOf(JsonDocument.Parse(await approved.Content.ReadAsStringAsync()).RootElement));
    }

    // ProposalSource alone lowers the link's scope: the REST route keeps `write:direct`.
    [Fact]
    public async Task A_write_only_token_cannot_link_a_document_through_the_rest_route()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        var document = await UploadedAsync(app, app.AdultA, Pdf);
        var token = await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Propose);

        var response = await app.BearerAsync(token.Secret, HttpMethod.Post, $"{Root}/{document}/links",
            new { targetKind = TargetKinds.CoreItem, targetId = envelope, role = DocumentRoles.Statement });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await LinksAsync(app, app.AdultA, document));
    }

    [Fact]
    public async Task A_refused_payload_or_a_conforme_answer_leaves_no_link()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        var source = await McpSeed.IncomeSourceAsync(app, app.AdultA); // 2 300 € net a month from 2025-01-01
        var document = await UploadedAsync(app, app.AdultA, Pdf);
        await using var client = await ClientAsync(app);

        var refused = await CallAsync(client, "propose_statement", new { envelopeId = envelope, asOf = "2025-09-30", movements = new[] { new { on = "2025-09-06", kind = "contribution", amount = "1 234,56" } }, documentId = document });
        var same = await CallAsync(client, "propose_payslip",
            new { incomeSourceId = source, paidOn = "2025-09-28", net = new { amount = "2290.00", currency = "EUR" }, documentId = document });

        Assert.StartsWith("Refused", refused.Text);
        Assert.Contains("conforme", same.Text);
        Assert.Empty(await LinksAsync(app, app.AdultA, document));
        Assert.Empty(await ProposalsAsync(app, app.AdultA));
    }

    [Fact]
    public async Task Only_the_five_hand_written_tools_offer_a_document()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var client = await ClientAsync(app);

        var offering = (await client.ListToolsAsync())
            .Where(t => t.JsonSchema.TryGetProperty("properties", out var properties) && properties.TryGetProperty("documentId", out _))
            .Select(t => t.Name).Order().ToArray();

        Assert.Equal(["propose_amortization", "propose_holdings_snapshot", "propose_invoice", "propose_payslip", "propose_statement"], offering);
    }
}
