using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nid.Core.Application;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using UnambitiousFx.Functional.Failures;
using static Nid.TestSupport.ProposalKindHelpers;

namespace Nid.Core.IntegrationTests.Proposals;

/// <summary>
/// Spec 4D2 §3: the bank's table proposed on a loan — checked by 3F2's rules, compared with the calculation, approved as the
/// reference or not. The golden loan (loans.md §4) falls due on the 15th from 2020-11-15.
/// </summary>
public class AmortizationProposalTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Kind = "realestate.import_amortization";

    private static Dictionary<string, object?> R(string dueDate, string payment, string interest, string principal, string crd)
        => new() { ["dueDate"] = dueDate, ["payment"] = payment, ["interest"] = interest, ["principal"] = principal, ["crd"] = crd };

    /// <summary>The golden case's first three payments, as <see cref="LoanApi.BankTable"/> gives them.</summary>
    private static List<Dictionary<string, object?>> GoldenRows() =>
    [
        R("2020-11-15", "1159.92", "583.33", "576.59", "199423.41"),
        R("2020-12-15", "1159.92", "581.65", "578.27", "198845.14"),
        R("2021-01-15", "1159.92", "579.96", "579.96", "198265.18"),
    ];

    /// <summary>The bank owes 1 000 less after the third payment: the balance diverges on 2021-01-15.</summary>
    private static List<Dictionary<string, object?>> Diverging()
    {
        var rows = GoldenRows();
        rows[2]["crd"] = "197265.18";
        return rows;
    }

    private static object Table(Guid loan, object rows, bool? makeReference = null)
        => new
        {
            kind = Kind, targetItemId = loan, sourceLabel = "Tableau d’amortissement",
            payload = makeReference is { } make ? (object)new { rows, makeReference = make } : new { rows },
        };

    private static Task<HttpResponseMessage> PostAsync(NidApp app, object body) => app.RawAsync(app.AdultA, HttpMethod.Post, ProposalKindHelpers.Proposals, body);

    private static async Task<int> PendingAsync(NidApp app)
        => JsonDocument.Parse(await app.GetRawAsync(app.AdultA, ProposalKindHelpers.Proposals)).RootElement.GetProperty("items").GetArrayLength();

    private static async Task AssertRefusedAtAsync(HttpResponseMessage response, string path, string code)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = (await JsonOf(response)).GetProperty("errors")[0];
        Assert.Equal((path, code), (error.GetProperty("path").GetString(), error.GetProperty("code").GetString()));
    }

    private static async Task StoreAsReferenceAsync(NidApp app, Guid loan)
    {
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{LoanApi.Loans}/{loan}/bank-schedule",
            new { text = LoanApi.BankTable, version = await LoanApi.ContractVersionAsync(app, loan) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{LoanApi.Loans}/{loan}", new { scheduleSource = "bank" })).StatusCode);
    }

    [Fact]
    public async Task A_table_is_proposed_with_its_comparison_and_approved_as_the_reference()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await LoanApi.NewAsync(app, LoanApi.Golden(app));
        var rows = GoldenRows();
        rows[0]["insurance"] = "25.00"; // a table that details the insurance keeps it

        // D4: a submitter's makeReference is dropped; approved unchanged, the table becomes the reference.
        var proposal = await SubmitAsync(app, Table(loan, rows, makeReference: false));
        Assert.Equal(loan.ToString(), proposal.GetProperty("subjectKey").GetString());
        Assert.Empty(proposal.GetProperty("warnings").EnumerateArray());
        Assert.False(proposal.GetProperty("payload").TryGetProperty("makeReference", out _));

        var preview = (await ReadAsync(app, proposal)).GetProperty("preview");
        var summary = preview.GetProperty("summary");
        Assert.Equal(("EUR", "0.00", 3), (preview.GetProperty("currency").GetString(), summary.GetProperty("maxGap").GetString(), preview.GetProperty("count").GetInt32()));
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("divergesOn").ValueKind);
        var reference = preview.GetProperty("reference");
        Assert.Equal(("computed", "bank"), (reference.GetProperty("current").GetString(), reference.GetProperty("proposed").GetString()));
        Assert.Equal(["2020-11-15", "2020-12-15", "2021-01-15"], preview.GetProperty("first").EnumerateArray().Select(r => r.GetProperty("dueDate").GetString()));
        Assert.Empty(preview.GetProperty("last").EnumerateArray());
        Assert.Equal(("2020-11-15", "2021-01-15"), (preview.GetProperty("range").GetProperty("from").GetString(), preview.GetProperty("range").GetProperty("to").GetString()));

        var approved = await ApproveAsync(app, proposal);

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal(loan, (await JsonOf(approved)).GetProperty("appliedRefs")[0].GetGuid());
        var detail = await LoanApi.DetailAsync(app, loan);
        Assert.Equal("bank", LoanApi.S(detail, "scheduleSource"));
        var stored = detail.GetProperty("bankSchedule").GetProperty("rows");
        Assert.Equal((3, "25.00"), (stored.GetArrayLength(), LoanApi.S(stored[0], "insurance")));
    }

    [Fact]
    public async Task Unchecking_the_reference_stores_the_table_and_keeps_the_calculation()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await LoanApi.NewAsync(app, LoanApi.Golden(app));
        var proposal = await SubmitAsync(app, Table(loan, GoldenRows()));
        var edited = JsonNode.Parse(proposal.GetProperty("payload").GetRawText())!;
        edited["makeReference"] = false;

        var approved = await ApproveAsync(app, proposal, edited);

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.False((await JsonOf(approved)).GetProperty("editedPayload").GetProperty("makeReference").GetBoolean());
        var detail = await LoanApi.DetailAsync(app, loan);
        Assert.Equal(("computed", 3), (LoanApi.S(detail, "scheduleSource"), detail.GetProperty("bankSchedule").GetProperty("rows").GetArrayLength()));
    }

    /// <summary>D6: the stored rows on a loan still on the calculation are proposed (to make them the reference); once the reference, conforme.</summary>
    [Fact]
    public async Task The_stored_table_is_conforme_only_once_it_is_the_reference()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await LoanApi.NewAsync(app, LoanApi.Golden(app));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{LoanApi.Loans}/{loan}/bank-schedule",
            new { text = LoanApi.BankTable, version = await LoanApi.ContractVersionAsync(app, loan) })).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await PostAsync(app, Table(loan, GoldenRows()))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{LoanApi.Loans}/{loan}", new { scheduleSource = "bank" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, Table(loan, GoldenRows()))).StatusCode);
    }

    [Fact]
    public async Task A_diverging_table_is_proposed_with_schedule_mismatch()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await LoanApi.NewAsync(app, LoanApi.Golden(app));

        var proposal = await SubmitAsync(app, Table(loan, Diverging()));

        var warning = Assert.Single(proposal.GetProperty("warnings").EnumerateArray());
        Assert.Equal(("schedule_mismatch", "warning", "payload.rows[2]"),
            (warning.GetProperty("code").GetString(), warning.GetProperty("severity").GetString(), warning.GetProperty("field").GetString()));
        Assert.Contains("2021-01-15", warning.GetProperty("message").GetString());
        var summary = (await ReadAsync(app, proposal)).GetProperty("preview").GetProperty("summary");
        Assert.Equal(("2021-01-15", "1000.00", "2021-01-15"),
            (summary.GetProperty("divergesOn").GetString(), summary.GetProperty("maxGap").GetString(), summary.GetProperty("maxGapOn").GetString()));
    }

    // Review Focus 1 and 5.
    [Fact]
    public async Task Rows_breaking_a_rule_are_refused_at_their_path_and_nothing_is_proposed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await LoanApi.NewAsync(app, LoanApi.Golden(app));
        List<Dictionary<string, object?>> With(int k, string column, string value)
        {
            var rows = GoldenRows();
            rows[k][column] = value;
            return rows;
        }
        var gap = GoldenRows();
        gap.RemoveAt(1);

        await AssertRefusedAtAsync(await PostAsync(app, Table(loan, gap)), "payload.rows[1].dueDate", "realestate.bank_schedule_invalid");
        await AssertRefusedAtAsync(await PostAsync(app, Table(loan, With(0, "payment", "1209.92"))), "payload.rows[0].payment", "realestate.bank_schedule_invalid");
        await AssertRefusedAtAsync(await PostAsync(app, Table(loan, With(1, "crd", "199500.00"))), "payload.rows[1].crd", "realestate.bank_schedule_invalid");
        await AssertRefusedAtAsync(await PostAsync(app, Table(loan, With(0, "payment", "1159,92"))), "payload.rows[0].payment", "request.invalid");
        await AssertRefusedAtAsync(await PostAsync(app, Table(loan, new[]
        {
            new { dueDate = "2020-11-15", payment = 1159.92m, interest = 583.33m, principal = 576.59m, crd = 199423.41m },
        })), "payload.rows[0].payment", "request.invalid");
        await AssertRefusedAtAsync(await PostAsync(app, Table(loan, new object[] { GoldenRows()[0], "2020-12-15;1159.92" })), "payload.rows[1]", "request.invalid");
        await AssertRefusedAtAsync(await PostAsync(app, Table(loan, Enumerable.Repeat(GoldenRows()[0], 1201).ToList())), "payload.rows", "request.invalid");
        await AssertRefusedAtAsync(await PostAsync(app, Table(loan, Array.Empty<object>())), "payload.rows", "request.invalid");

        var garbage = await PostAsync(app, Table(loan, Enumerable.Range(0, 200).Select(_ => R("x", "x", "x", "x", "x")).ToList()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, garbage.StatusCode);
        Assert.Equal(50, (await JsonOf(garbage)).GetProperty("errors").GetArrayLength());

        Assert.Equal(0, await PendingAsync(app));
    }

    // Review Focus 2.
    [Fact]
    public async Task A_table_starting_after_the_first_payment_is_proposed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await LoanApi.NewAsync(app, LoanApi.Golden(app));

        var proposal = await SubmitAsync(app, Table(loan, GoldenRows().Skip(2).ToList()));

        Assert.Equal(1, (await ReadAsync(app, proposal)).GetProperty("preview").GetProperty("count").GetInt32());
    }

    // Review Focus 4.
    [Fact]
    public async Task A_table_approved_on_a_loan_already_on_the_bank_replaces_it_and_stays_the_reference()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await LoanApi.NewAsync(app, LoanApi.Golden(app));
        await StoreAsReferenceAsync(app, loan);
        var proposal = await SubmitAsync(app, Table(loan, Diverging()));
        var reference = (await ReadAsync(app, proposal)).GetProperty("preview").GetProperty("reference");
        Assert.Equal(("bank", "bank"), (reference.GetProperty("current").GetString(), reference.GetProperty("proposed").GetString()));

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(app, proposal)).StatusCode);

        var detail = await LoanApi.DetailAsync(app, loan);
        Assert.Equal(("bank", "197265.18"), (LoanApi.S(detail, "scheduleSource"), LoanApi.S(detail.GetProperty("bankSchedule").GetProperty("rows")[2], "crd")));
    }

    [Fact]
    public async Task A_newer_table_supersedes_the_pending_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await LoanApi.NewAsync(app, LoanApi.Golden(app));

        var first = await SubmitAsync(app, Table(loan, GoldenRows()));
        var second = await SubmitAsync(app, Table(loan, Diverging()));

        var old = (await ReadAsync(app, first)).GetProperty("proposal");
        Assert.Equal("superseded", old.GetProperty("status").GetString());
        Assert.Equal(second.GetProperty("id").GetGuid(), old.GetProperty("supersededById").GetGuid());
    }

    [Fact]
    public async Task A_loan_edited_so_the_table_no_longer_fits_fails_the_approval_and_stays_pending()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await LoanApi.NewAsync(app, LoanApi.Golden(app));
        var proposal = await SubmitAsync(app, Table(loan, GoldenRows()));
        var contract = LoanApi.Contract();
        contract["firstPaymentDate"] = "2020-11-20"; // the loan now falls due on the 20th
        contract["version"] = await LoanApi.ContractVersionAsync(app, loan);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{LoanApi.Loans}/{loan}/contract", contract)).StatusCode);

        await AssertRefusedAtAsync(await ApproveAsync(app, proposal), "payload.rows[0].dueDate", "realestate.bank_schedule_invalid");

        var after = (await ReadAsync(app, proposal)).GetProperty("proposal");
        Assert.Equal("pending", after.GetProperty("status").GetString());
        Assert.Contains(after.GetProperty("warnings").EnumerateArray(), w => w.GetProperty("code").GetString() == ProposalCodes.ApplyFailed);
        Assert.Equal(JsonValueKind.Null, (await LoanApi.DetailAsync(app, loan)).GetProperty("bankSchedule").ValueKind);
    }

    [Fact]
    public async Task A_direct_write_needs_write_direct_and_makes_the_table_the_reference()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await LoanApi.NewAsync(app, LoanApi.Golden(app));
        var command = new ApplyDirectCommand(Kind, new ItemId(loan), JsonSerializer.SerializeToElement(new { rows = GoldenRows(), makeReference = false }));

        var refused = await DirectWriteTests.AsTokenAsync(app, new HashSet<string> { ScopeNames.Read, ScopeNames.Write }, command);
        Assert.True(refused.TryGetFailure(out var failure));
        Assert.IsType<UnauthorizedFailure>(failure);

        var written = await DirectWriteTests.AsTokenAsync(app, DirectWriteTests.Direct, command);
        Assert.True(written.TryGetValue(out var write));
        Assert.Equal(("applied", loan), (write!.Outcome, Assert.Single(write.AppliedRefs)));
        Assert.Equal("bank", LoanApi.S(await LoanApi.DetailAsync(app, loan), "scheduleSource")); // the submitter's `false` is dropped
    }

    [Fact]
    public async Task A_makeReference_that_is_not_a_boolean_is_ignored_on_submit_and_refused_on_an_edit()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await LoanApi.NewAsync(app, LoanApi.Golden(app));

        var proposal = await SubmitAsync(app, new
        {
            kind = Kind, targetItemId = loan, sourceLabel = "t", payload = new { rows = GoldenRows(), makeReference = "no" },
        });
        Assert.False(proposal.GetProperty("payload").TryGetProperty("makeReference", out _));

        var edited = JsonNode.Parse(proposal.GetProperty("payload").GetRawText())!;
        edited["makeReference"] = "yes";
        await AssertRefusedAtAsync(await ApproveAsync(app, proposal, edited), "payload.makeReference", "request.invalid");
        Assert.Equal("pending", (await ReadAsync(app, proposal)).GetProperty("proposal").GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_direct_write_sent_twice_is_conforme_the_second_time()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await LoanApi.NewAsync(app, LoanApi.Golden(app));
        var command = new ApplyDirectCommand(Kind, new ItemId(loan), JsonSerializer.SerializeToElement(new { rows = GoldenRows() }));

        Assert.True((await DirectWriteTests.AsTokenAsync(app, DirectWriteTests.Direct, command)).TryGetValue(out var first));
        Assert.True((await DirectWriteTests.AsTokenAsync(app, DirectWriteTests.Direct, command)).TryGetValue(out var second));

        Assert.Equal(("applied", "conforme"), (first!.Outcome, second!.Outcome));
        Assert.Empty(second.AppliedRefs);
    }

    [Fact]
    public async Task Approving_a_table_that_became_conforme_writes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await LoanApi.NewAsync(app, LoanApi.Golden(app));
        var proposal = await SubmitAsync(app, Table(loan, GoldenRows()));
        var command = new ApplyDirectCommand(Kind, new ItemId(loan), JsonSerializer.SerializeToElement(new { rows = GoldenRows() }));
        Assert.True((await DirectWriteTests.AsTokenAsync(app, DirectWriteTests.Direct, command)).TryGetValue(out _));

        var approved = await ApproveAsync(app, proposal);

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Empty((await JsonOf(approved)).GetProperty("appliedRefs").EnumerateArray());
        var rows = (await LoanApi.DetailAsync(app, loan)).GetProperty("bankSchedule").GetProperty("rows");
        Assert.Equal(3, rows.GetArrayLength()); // the direct write's table, untouched by the approval
        Assert.Equal(("bank", "198265.18"), (LoanApi.S(await LoanApi.DetailAsync(app, loan), "scheduleSource"), LoanApi.S(rows[2], "crd")));
    }

    /// <summary>D5: the only edit is the reference; rows that differ from the submitted ones are refused.</summary>
    [Fact]
    public async Task An_edit_that_changes_the_rows_is_refused_and_one_that_keeps_them_is_accepted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await LoanApi.NewAsync(app, LoanApi.Golden(app));
        var proposal = await SubmitAsync(app, Table(loan, GoldenRows()));
        var changed = JsonNode.Parse(proposal.GetProperty("payload").GetRawText())!;
        changed["rows"]![2]!["crd"] = "197265.18"; // a valid table, but not the submitted one

        await AssertRefusedAtAsync(await ApproveAsync(app, proposal, changed), "payload.rows", "request.invalid");

        var after = (await ReadAsync(app, proposal)).GetProperty("proposal"); // the refusal's warning bumped its version
        Assert.Equal("pending", after.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, (await LoanApi.DetailAsync(app, loan)).GetProperty("bankSchedule").ValueKind);

        var same = JsonNode.Parse(proposal.GetProperty("payload").GetRawText())!;
        same["makeReference"] = false;
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(app, after, same)).StatusCode);
    }

    [Fact]
    public async Task A_five_row_preview_shows_three_first_and_two_last_rows()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await LoanApi.NewAsync(app, LoanApi.Golden(app));
        var five = GoldenRows();
        five.Add(R("2021-02-15", "1159.92", "578.27", "581.65", "197683.53"));
        five.Add(R("2021-03-15", "1159.92", "576.58", "583.34", "197100.19"));

        var preview = (await ReadAsync(app, await SubmitAsync(app, Table(loan, five)))).GetProperty("preview");

        Assert.Equal(["2020-11-15", "2020-12-15", "2021-01-15"], preview.GetProperty("first").EnumerateArray().Select(r => r.GetProperty("dueDate").GetString()));
        Assert.Equal(["2021-02-15", "2021-03-15"], preview.GetProperty("last").EnumerateArray().Select(r => r.GetProperty("dueDate").GetString()));
    }
}
