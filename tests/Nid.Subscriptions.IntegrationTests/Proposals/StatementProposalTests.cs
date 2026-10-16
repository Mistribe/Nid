using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nid.Core.Contracts;
using Nid.Subscriptions.IntegrationTests.Mcp;
using Nid.TestSupport;
using static Nid.Subscriptions.IntegrationTests.Proposals.ProposalKindTests;

namespace Nid.Subscriptions.IntegrationTests.Proposals;

/// <summary>Spec 4D1 §2: a statement for one envelope — its value and its movements. The envelope: opening 20 000 € and a value of 21 000 € on 2025-01-31.</summary>
[Collection("postgres")]
public class StatementProposalTests(PostgresFixture fixture)
{
    private const string Kind = "investments.record_statement";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static Dictionary<string, object?> Movement(
        string on = "2025-09-05", string kind = "contribution", string amount = "150.00", string? label = "Versement", string? reference = null)
        => new() { ["on"] = on, ["kind"] = kind, ["amount"] = amount, ["label"] = label, ["ref"] = reference };

    private static object Statement(Guid envelope, object[] movements, string asOf = "2025-09-30", string? value = "23500.00", string currency = "EUR")
        => new
        {
            kind = Kind, targetItemId = envelope, sourceLabel = "Relevé PEA septembre",
            payload = new { asOf, value = value is null ? null : new { amount = value, currency }, movements },
        };

    [Fact]
    public async Task A_statement_is_previewed_then_approval_records_its_movements_and_its_value()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);

        var proposal = await SubmitAsync(app, Statement(envelope,
            [Movement(reference: "R-1"), Movement(on: "2025-09-20", kind: "withdrawal", amount: "40.00", label: "Retrait")]));
        Assert.Equal("2025-09-30", proposal.GetProperty("subjectKey").GetString());
        Assert.Empty(proposal.GetProperty("warnings").EnumerateArray());

        var preview = (await ReadAsync(app, proposal)).GetProperty("preview");
        Assert.Equal("2025-01-31", preview.GetProperty("value").GetProperty("previous").GetProperty("asOf").GetString());
        Assert.Equal("23500.00", preview.GetProperty("value").GetProperty("proposed").GetProperty("amount").GetProperty("amount").GetString());
        var first = preview.GetProperty("movements")[0];
        Assert.Equal((0, "contribution", "EUR", false), (first.GetProperty("index").GetInt32(), first.GetProperty("kind").GetString(),
            first.GetProperty("amount").GetProperty("currency").GetString(), first.GetProperty("recorded").GetBoolean()));

        var approved = await JsonOf(await ApproveAsync(app, proposal));
        Assert.Equal("approved", approved.GetProperty("status").GetString());
        Assert.Equal(3, approved.GetProperty("appliedRefs").GetArrayLength());
        var detail = await EnvelopeAsync(app, envelope);
        var flows = detail.GetProperty("flows").EnumerateArray().ToList();
        Assert.Contains(flows, f => f.GetProperty("externalRef").GetString() == "R-1" && f.GetProperty("note").GetString() == "Versement");
        Assert.Contains(flows, f => f.GetProperty("kind").GetString() == "withdrawal" && f.GetProperty("occurredOn").GetString() == "2025-09-20");
        Assert.Contains(detail.GetProperty("valuations").EnumerateArray(), v => v.GetProperty("amount").GetProperty("amount").GetString() == "23500.0000");
    }

    [Fact]
    public async Task Without_an_edit_the_recorded_movements_are_left_out()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        await RecordFlowAsync(app, envelope, new { occurredOn = "2025-09-05", kind = "contribution", amount = new { amount = "150.00", currency = "EUR" } });

        var proposal = await SubmitAsync(app, Statement(envelope, [Movement(), Movement(on: "2025-09-12", amount: "60.00")], value: null));
        var approved = await JsonOf(await ApproveAsync(app, proposal));

        Assert.Equal(1, approved.GetProperty("appliedRefs").GetArrayLength());
        var flows = (await EnvelopeAsync(app, envelope)).GetProperty("flows").EnumerateArray().ToList();
        Assert.Single(flows, f => f.GetProperty("occurredOn").GetString() == "2025-09-05");
        Assert.Single(flows, f => f.GetProperty("occurredOn").GetString() == "2025-09-12");
    }

    // Review Focus 2.
    [Fact]
    public async Task Recorded_movements_are_warned_and_a_rechecked_reference_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        await RecordFlowAsync(app, envelope, new { occurredOn = "2025-09-05", kind = "contribution", amount = new { amount = "150.00", currency = "EUR" } });
        await RecordFlowAsync(app, envelope, new { occurredOn = "2025-08-01", kind = "contribution", amount = new { amount = "99.00", currency = "EUR" }, externalRef = "R-9" });

        var proposal = await SubmitAsync(app, Statement(envelope,
            [Movement(), Movement(on: "2025-09-10", amount: "75.00", reference: "R-9"), Movement(on: "2025-09-12", amount: "60.00")], value: null));
        Assert.Equal("payload.movements[0],payload.movements[1]", string.Join(",", proposal.GetProperty("warnings").EnumerateArray()
            .Where(w => w.GetProperty("code").GetString() == ProposalCodes.AlreadyRecorded).Select(w => w.GetProperty("field").GetString())));
        var recorded = (await ReadAsync(app, proposal)).GetProperty("preview").GetProperty("movements").EnumerateArray()
            .Select(m => m.GetProperty("recorded").GetBoolean()).ToArray();
        Assert.Equal([true, true, false], recorded);

        var refused = await ApproveAsync(app, proposal, Edited(proposal)); // every movement re-checked
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode); // core.external_ref_taken answers 409
        Assert.Equal("payload.movements[1].ref", (await JsonOf(refused)).GetProperty("errors")[0].GetProperty("path").GetString());

        var reread = (await ReadAsync(app, proposal)).GetProperty("proposal");
        Assert.Equal("pending", reread.GetProperty("status").GetString());
        var approved = await ApproveAsync(app, reread, Edited(reread, 1)); // the matched one re-checked, the reference left out
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal(2, (await JsonOf(approved)).GetProperty("appliedRefs").GetArrayLength());
        var flows = (await EnvelopeAsync(app, envelope)).GetProperty("flows").EnumerateArray().ToList();
        Assert.Equal(2, flows.Count(f => f.GetProperty("occurredOn").GetString() == "2025-09-05"));
        Assert.Single(flows, f => f.GetProperty("externalRef").GetString() == "R-9");
    }

    // Review Focus 3.
    [Fact]
    public async Task Statements_the_envelope_refuses_answer_at_their_path()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        object[] many = [.. Enumerable.Range(0, 201).Select(i => Movement(amount: $"{i + 1}.00"))];

        foreach (var (body, path) in new (object Body, string Path)[]
                 {
                     (Statement(envelope, [Movement(on: "2025-10-01")]), "payload.movements[0].on"),
                     (Statement(envelope, [Movement(kind: "opening")]), "payload.movements[0].kind"),
                     (Statement(envelope, [Movement(amount: "-5.00")]), "payload.movements[0].amount"),
                     (Statement(envelope, [Movement(amount: "1 234,56")]), "payload.movements[0].amount"),
                     (Statement(envelope, many), "payload.movements"),
                     (Statement(envelope, [Movement(reference: "R-1"), Movement(on: "2025-09-06", reference: "R-1")]), "payload.movements[1].ref"),
                     (Statement(envelope, [Movement(label: new string('x', 201))]), "payload.movements[0].label"),
                     (Statement(envelope, [], currency: "USD"), "payload.value.currency"),
                     (Statement(envelope, [], value: null), "payload.movements"),
                     (Statement(envelope, [], asOf: "2025-10-16"), "payload.asOf"),
                 })
        {
            var response = await app.RawAsync(app.AdultA, HttpMethod.Post, ProposalRouteTests.Proposals, body);
            Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{path}: {response.StatusCode}");
            Assert.Equal(path, (await JsonOf(response)).GetProperty("errors")[0].GetProperty("path").GetString());
        }
    }

    [Fact]
    public async Task Another_statement_for_the_same_date_supersedes_the_pending_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);

        var first = await SubmitAsync(app, Statement(envelope, [Movement()]));
        var second = await SubmitAsync(app, Statement(envelope, [Movement(), Movement(on: "2025-09-12", amount: "60.00")]));

        var old = (await ReadAsync(app, first)).GetProperty("proposal");
        Assert.Equal("superseded", old.GetProperty("status").GetString());
        Assert.Equal(second.GetProperty("id").GetGuid(), old.GetProperty("supersededById").GetGuid());
    }
}
