using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Investments.Application;
using Nid.TestSupport;
using static Nid.TestSupport.ProposalKindHelpers;

namespace Nid.Core.IntegrationTests.Proposals;

/// <summary>Issue #34 §6: a statement's holdings for one envelope — lines matched by ISIN then name, new holdings, movements, the 30 % flag.</summary>
public class HoldingsSnapshotProposalTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Kind = "investments.record_holdings_snapshot";
    private const string FundIsin = "FR0000120271";

    private static string Url(ItemId id) => $"/api/v1/investments/envelopes/{id.Value}";

    private static object Eur(string amount) => new { amount, currency = "EUR" };

    /// <summary>A life insurance with a euro fund and fund A (by ISIN), each in a snapshot of 2025-06-30.</summary>
    private static async Task<(ItemId Id, Guid Euro, Guid Fund)> EnvelopeAsync(NidApp app)
    {
        var id = await app.NewEnvelopeAsync("Assurance-vie", app.AdultA, Visibility.Shared);
        async Task<Guid> Add(object body)
            => (await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/holdings", body))).GetProperty("id").GetGuid();
        var euro = await Add(new { name = "Fonds euros", kind = "euro_fund" });
        var fund = await Add(new { name = "Fonds A", kind = "fund", isin = FundIsin });
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/holding-snapshots", new
        {
            asOf = "2025-06-30",
            lines = new object[] { new { holdingId = euro, value = Eur("1000.00") }, new { holdingId = fund, units = "10", unitPrice = "100", value = Eur("1000.00") } },
        })).EnsureSuccessStatusCode();
        return (id, euro, fund);
    }

    private static Dictionary<string, object?> Line(string name, string value, string? isin = null, string? kind = null, string? units = null, string? price = null)
        => new() { ["name"] = name, ["isin"] = isin, ["kind"] = kind, ["units"] = units, ["unitPrice"] = price, ["value"] = value };

    private static Dictionary<string, object?> Move(string holding, string kind, string amount, string on = "2025-09-15", string? units = null, string? reference = null)
        => new() { ["holding"] = holding, ["on"] = on, ["kind"] = kind, ["amount"] = amount, ["units"] = units, ["ref"] = reference };

    private static object Proposal(ItemId id, object[] lines, object[]? movements = null, bool recordValuation = false, string asOf = "2025-09-30")
        => new
        {
            kind = Kind, targetItemId = id.Value, sourceLabel = "Information trimestrielle",
            payload = new { asOf, sourceLabel = "Information trimestrielle", lines, movements = movements ?? [], recordValuation },
        };

    private static async Task<JsonElement> HoldingsAsync(NidApp app, ItemId id)
        => JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"{Url(id)}/holdings")).RootElement;

    [Fact]
    public async Task A_statement_is_matched_by_isin_and_name_proposes_a_new_holding_and_approval_writes_it_all()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, euro, fund) = await EnvelopeAsync(app);

        var proposal = await SubmitAsync(app, Proposal(id,
            [
                Line("Fonds euros", "1010.00"),
                Line("Fonds A part C", "1050.00", isin: FundIsin.ToLowerInvariant(), units: "10.5", price: "100"),
                Line("Nouveau fonds", "500.00", kind: "etf", units: "5", price: "100"),
            ],
            [Move(FundIsin, "allocation_in", "50.00", units: "0.5"), Move("Nouveau fonds", "allocation_in", "500.00", units: "5"), Move("Fonds euros", "interest", "10.00")],
            recordValuation: true));
        Assert.Equal("2025-09-30", proposal.GetProperty("subjectKey").GetString());

        var preview = (await ReadAsync(app, proposal)).GetProperty("preview");
        Assert.Equal([false, false, true], preview.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("isNew").GetBoolean()));
        Assert.Equal("fund", preview.GetProperty("lines")[1].GetProperty("kind").GetString());
        Assert.Equal("1000.0000", preview.GetProperty("lines")[1].GetProperty("previous").GetProperty("value").GetProperty("amount").GetString());
        Assert.Equal("2560.00", preview.GetProperty("total").GetProperty("amount").GetString());
        Assert.Equal("Fonds A", preview.GetProperty("movements")[0].GetProperty("holding").GetString());

        var approved = await JsonOf(await ApproveAsync(app, proposal));
        Assert.Equal("approved", approved.GetProperty("status").GetString());
        Assert.Equal(6, approved.GetProperty("appliedRefs").GetArrayLength()); // the new holding, the snapshot, its value, three movements

        var holdings = await HoldingsAsync(app, id);
        Assert.Equal("2025-09-30", holdings.GetProperty("holdingsAsOf").GetString());
        Assert.Equal("2560.0000", holdings.GetProperty("total").GetProperty("amount").GetString());
        Assert.Equal("0.0000", holdings.GetProperty("unallocated").GetProperty("amount").GetString()); // recordValuation
        var byName = holdings.GetProperty("holdings").EnumerateArray().ToDictionary(h => h.GetProperty("name").GetString()!);
        Assert.Equal(3, byName.Count);
        Assert.Equal(("etf", "500.0000"), (byName["Nouveau fonds"].GetProperty("kind").GetString(),
            byName["Nouveau fonds"].GetProperty("line").GetProperty("value").GetProperty("amount").GetString()));
        Assert.Equal("10.500000", byName["Fonds A"].GetProperty("line").GetProperty("units").GetString());
        var moves = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"{Url(id)}/holdings/{fund}/movements")).RootElement.GetProperty("items");
        Assert.Equal(("allocation_in", "0.500000"), (moves[0].GetProperty("kind").GetString(), moves[0].GetProperty("units").GetString()));
        Assert.Single(JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"{Url(id)}/holdings/{euro}/movements")).RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Recorded_movements_are_warned_and_left_out()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, _, fund) = await EnvelopeAsync(app);
        foreach (var body in new object[]
                 {
                     new { occurredOn = "2025-09-15", kind = "fee", units = "-0.1", amount = Eur("10.00") },
                     new { occurredOn = "2025-08-01", kind = "fee", amount = Eur("3.00"), externalRef = "F-1" },
                 })
            (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/holdings/{fund}/movements", body)).EnsureSuccessStatusCode();

        var proposal = await SubmitAsync(app, Proposal(id, [Line("Fonds A", "990.00", units: "9.9", price: "100")],
            [Move("Fonds A", "fee", "10.00", units: "0.1"), Move("Fonds A", "fee", "3.00", on: "2025-09-01", reference: "F-1"), Move("Fonds A", "fee", "10.00", units: "0.1")]));
        Assert.Equal("payload.movements[0],payload.movements[1]", string.Join(",", proposal.GetProperty("warnings").EnumerateArray()
            .Where(w => w.GetProperty("code").GetString() == ProposalCodes.AlreadyRecorded).Select(w => w.GetProperty("field").GetString())));
        Assert.Equal([true, true, false], (await ReadAsync(app, proposal)).GetProperty("preview").GetProperty("movements").EnumerateArray()
            .Select(m => m.GetProperty("recorded").GetBoolean()));

        var approved = await JsonOf(await ApproveAsync(app, proposal));
        Assert.Equal(2, approved.GetProperty("appliedRefs").GetArrayLength()); // the snapshot and the second identical fee
        var moves = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"{Url(id)}/holdings/{fund}/movements")).RootElement.GetProperty("items");
        Assert.Equal(3, moves.GetArrayLength());
    }

    [Fact]
    public async Task A_line_moving_more_than_30_percent_net_of_its_movements_is_flagged()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, _, _) = await EnvelopeAsync(app);

        // Fund A: 1000 → 1500 with 400 paid in, +10 %; the euro fund: 1000 → 1400 with nothing, +40 %.
        var proposal = await SubmitAsync(app, Proposal(id,
            [Line("Fonds euros", "1400.00"), Line("Fonds A", "1500.00", isin: FundIsin)], [Move("Fonds A", "allocation_in", "400.00")]));

        Assert.Equal(["payload.lines[0].value"], proposal.GetProperty("warnings").EnumerateArray()
            .Where(w => w.GetProperty("code").GetString() == HoldingsSnapshotProposal.LargeChange).Select(w => w.GetProperty("field").GetString()));
        Assert.Equal([true, false], (await ReadAsync(app, proposal)).GetProperty("preview").GetProperty("lines").EnumerateArray()
            .Select(l => l.GetProperty("largeChange").GetBoolean()));
    }

    [Fact]
    public async Task Statements_the_envelope_refuses_answer_at_their_path()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, _, _) = await EnvelopeAsync(app);

        foreach (var (body, path) in new (object Body, string Path)[]
                 {
                     (Proposal(id, [Line("Inconnu", "10.00")]), "payload.lines[0].kind"),
                     (Proposal(id, [Line("Inconnu", "10.00", kind: "crypto")]), "payload.lines[0].kind"),
                     (Proposal(id, [Line("Inconnu", "10.00", kind: "fund", isin: "FR0000120272")]), "payload.lines[0].isin"),
                     (Proposal(id, [Line("Fonds euros", "10.00", units: "1")]), "payload.lines[0].units"),
                     (Proposal(id, [Line("Fonds euros", "10.00"), Line("Fonds euros", "11.00")]), "payload.lines[1].name"),
                     (Proposal(id, [Line("Fonds euros", "-1")]), "payload.lines[0].value"),
                     (Proposal(id, []), "payload.lines"),
                     (Proposal(id, [Line("Fonds euros", "10.00")], [Move("Autre", "fee", "1.00")]), "payload.movements[0].holding"),
                     (Proposal(id, [Line("Fonds euros", "10.00")], [Move("Fonds A", "switch_in", "1.00")]), "payload.movements[0].kind"),
                     (Proposal(id, [Line("Fonds euros", "10.00")], [Move("Fonds A", "fee", "1.00", on: "2025-10-01")]), "payload.movements[0].on"),
                     (Proposal(id, [Line("Fonds euros", "10.00")], [Move("Fonds euros", "interest", "1.00", units: "1")]), "payload.movements[0].units"),
                     (Proposal(id, [Line("Fonds euros", "10.00")], asOf: "2025-10-16"), "payload.asOf"),
                 })
        {
            var response = await app.RawAsync(app.AdultA, HttpMethod.Post, ProposalKindHelpers.Proposals, body);
            Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{path}: {response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
            Assert.Equal(path, (await JsonOf(response)).GetProperty("errors")[0].GetProperty("path").GetString());
        }
    }

    [Fact]
    public async Task Another_statement_for_the_same_date_supersedes_the_pending_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, _, _) = await EnvelopeAsync(app);

        var first = await SubmitAsync(app, Proposal(id, [Line("Fonds euros", "1010.00")]));
        var second = await SubmitAsync(app, Proposal(id, [Line("Fonds euros", "1020.00")]));

        Assert.Equal("superseded", (await ReadAsync(app, first)).GetProperty("proposal").GetProperty("status").GetString());
        Assert.Equal("pending", (await ReadAsync(app, second)).GetProperty("proposal").GetProperty("status").GetString());
    }
}
