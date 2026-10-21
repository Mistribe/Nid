using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Application;
using Nid.RealEstate.Contracts;
using Nid.TestSupport;
using NodaTime;
using static Nid.Subscriptions.IntegrationTests.RealEstate.LoanApi;

namespace Nid.Subscriptions.IntegrationTests.RealEstate;

/// <summary>Spec 3F2 §3: the bank's table pasted on a loan — previewed, stored, chosen as the reference, replaced and deleted.</summary>
public class BankScheduleEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static string Table(Guid id) => $"{Loans}/{id}/bank-schedule";

    private static Task<HttpResponseMessage> PreviewRawAsync(NidApp app, Guid id, object body, PersonId? actor = null)
        => app.RawAsync(actor ?? app.AdultA, HttpMethod.Post, $"{Table(id)}/preview", body);

    private static Task<HttpResponseMessage> PutAsync(NidApp app, Guid id, string text, uint version, PersonId? actor = null)
        => app.RawAsync(actor ?? app.AdultA, HttpMethod.Put, Table(id), new { text, version });

    private static async Task ChooseAsync(NidApp app, Guid id, string source)
        => Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{id}", new { scheduleSource = source })).StatusCode);

    /// <summary>Review Focus 1: the spreadsheet's copy, thousands after U+00A0, reads whole; nothing is stored.</summary>
    [Fact]
    public async Task A_pasted_table_is_read_and_compared_and_nothing_is_stored()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Golden(app));

        var response = await PreviewRawAsync(app, id, new { text = BankTable });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await JsonAsync(response);
        var rows = preview.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(["2020-11-15", "2020-12-15", "2021-01-15"], rows.Select(r => S(r, "dueDate")));
        Assert.Equal(("1159.92", "583.33", "576.59", "199423.41"), (S(rows[0], "payment"), S(rows[0], "interest"), S(rows[0], "principal"), S(rows[0], "crd")));
        Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("insurance").ValueKind);
        var comparison = preview.GetProperty("comparison");
        Assert.Equal(("0.00", "0.00", 0), (S(comparison, "maxGap"), S(comparison, "interestGap"), comparison.GetProperty("rowsBeyondRounding").GetInt32()));
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (comparison.GetProperty("divergesOn").ValueKind, comparison.GetProperty("maxGapOn").ValueKind));
        var detail = await DetailAsync(app, id);
        Assert.Equal(("computed", JsonValueKind.Null, JsonValueKind.Null),
            (S(detail, "scheduleSource"), detail.GetProperty("bankSchedule").ValueKind, detail.GetProperty("comparison").ValueKind));
    }

    /// <summary>Review Focus 4: a table replaced while the bank is the reference stays the reference; back to the calculation it is kept, compared, warned about.</summary>
    [Fact]
    public async Task A_table_is_stored_chosen_replaced_and_deleted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Golden(app));
        var version = await ContractVersionAsync(app, id);

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(app, id, BankTable, version)).StatusCode);

        var stored = await DetailAsync(app, id);
        var table = stored.GetProperty("bankSchedule");
        Assert.Equal((3, "2020-11-15", "2021-01-15"), (table.GetProperty("rows").GetArrayLength(), S(table, "firstDueOn"), S(table, "lastDueOn")));
        Assert.Equal(("computed", "0.00"), (S(stored, "scheduleSource"), S(stored.GetProperty("comparison"), "maxGap")));
        Assert.NotEqual(version, stored.GetProperty("contract").GetProperty("version").GetUInt32());

        await ChooseAsync(app, id, "bank");
        var schedule = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Loans}/{id}/schedule"))).GetProperty("rows");
        Assert.Equal([true, true, true, false], schedule.EnumerateArray().Take(4).Select(r => r.GetProperty("fromBank").GetBoolean()));
        Assert.Equal("bank", S(await DetailAsync(app, id), "scheduleSource"));

        var owing = BankTable.Replace("198 265,18", "197 265,18"); // the bank owes 1 000 less after the third payment
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(app, id, owing, await ContractVersionAsync(app, id))).StatusCode);
        var replaced = await DetailAsync(app, id);
        Assert.Equal(("bank", "197265.18"), (S(replaced, "scheduleSource"), S(replaced.GetProperty("bankSchedule").GetProperty("rows")[2], "crd")));
        Assert.NotEqual("162253.12", S(replaced.GetProperty("figures"), "balance"));

        await ChooseAsync(app, id, "computed");
        var back = await DetailAsync(app, id);
        Assert.Equal(("computed", "162253.12"), (S(back, "scheduleSource"), S(back.GetProperty("figures"), "balance")));
        Assert.Equal(3, back.GetProperty("bankSchedule").GetProperty("rows").GetArrayLength());
        Assert.Equal(("2021-01-15", "1000.00"), (S(back.GetProperty("comparison"), "divergesOn"), S(back.GetProperty("comparison"), "maxGap")));
        var warning = Only(back, "warnings");
        Assert.Equal(("schedule_mismatch", 3), (S(warning, "code"), warning.GetProperty("row").GetInt32()));

        await ChooseAsync(app, id, "bank");
        var deleted = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Table(id)}?version={await ContractVersionAsync(app, id)}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var after = await DetailAsync(app, id);
        Assert.Equal(("computed", JsonValueKind.Null), (S(after, "scheduleSource"), after.GetProperty("bankSchedule").ValueKind));
        Assert.Empty(after.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task The_bank_cannot_be_the_reference_without_a_table()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Golden(app));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.bank_schedule_missing", "scheduleSource"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{id}", new { scheduleSource = "bank" })));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_loan", "scheduleSource"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{id}", new { scheduleSource = "table" })));
    }

    /// <summary>Review Focus 5 among them: payments holding the insurance while the table has no insurance column.</summary>
    [Fact]
    public async Task Each_unreadable_cell_and_the_first_broken_rule_answer_at_their_line()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Golden(app));

        // Line 0 is the header, line 3 a blank line: the person counts them too.
        var unreadable = await PreviewRawAsync(app, id, new
        {
            text = "Date;Échéance;Intérêts;Capital;Capital restant dû\n15/11/2020;1159,92;583,33;576,59;199423,41\n"
                   + "pas une date;1159,92;581,65;578,27;198845,14\n\n15/01/2021;1 159.92,0;579,96;579,96;198265,18\n15/02/2021;1159,92;579,96\n",
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unreadable.StatusCode);
        Assert.Equal(
            [("realestate.bank_schedule_invalid", "rows[2].dueDate"), ("realestate.bank_schedule_invalid", "rows[4].payment"),
             ("realestate.bank_schedule_invalid", "rows[5]")],
            (await JsonAsync(unreadable)).GetProperty("errors").EnumerateArray().Select(e => (S(e, "code"), S(e, "path"))));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.bank_schedule_invalid", "rows[1].payment"),
            await RefusalAsync(await PreviewRawAsync(app, id, new { text = "Date;Échéance;Intérêts;Capital;CRD\n15/11/2020;1209,92;583,33;576,59;199423,41\n" })));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.bank_schedule_invalid", "rows[0].dueDate"),
            await RefusalAsync(await PreviewRawAsync(app, id, new { text = "16/11/2020;1159,92;583,33;576,59;199423,41" })));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.bank_schedule_invalid", "text"),
            await RefusalAsync(await PreviewRawAsync(app, id, new { text = "Date;Échéance\n" })));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "request.required", "text"), await RefusalAsync(await PreviewRawAsync(app, id, new { })));
    }

    /// <summary>A garbage paste within the 200 000 characters lists its first 50 unreadable cells, not a hundred thousand.</summary>
    [Fact]
    public async Task A_garbage_paste_lists_at_most_fifty_unreadable_cells()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Golden(app));

        var response = await PreviewRawAsync(app, id, new { text = string.Concat(Enumerable.Repeat("a;b;c;d;e\n", 20_000)) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var errors = (await JsonAsync(response)).GetProperty("errors").EnumerateArray().ToList();
        Assert.Equal(50, errors.Count);
        Assert.All(errors, e => Assert.Equal("realestate.bank_schedule_invalid", S(e, "code")));
    }

    /// <summary>All or nothing: a refused replacement leaves the stored table as it was.</summary>
    [Fact]
    public async Task A_refused_replacement_leaves_the_stored_table_intact()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Golden(app));
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(app, id, BankTable, await ContractVersionAsync(app, id))).StatusCode);
        var before = await DetailAsync(app, id);

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.bank_schedule_invalid", "rows[1].dueDate"),
            await RefusalAsync(await PutAsync(app, id, BankTable.Replace("15/11/2020", "pas une date"), await ContractVersionAsync(app, id))));

        var after = await DetailAsync(app, id);
        Assert.Equal(before.GetProperty("bankSchedule").GetRawText(), after.GetProperty("bankSchedule").GetRawText());
        Assert.Equal(before.GetProperty("contract").GetProperty("version").GetUInt32(), after.GetProperty("contract").GetProperty("version").GetUInt32());
    }

    [Fact]
    public async Task A_stale_version_conflicts_and_the_other_adult_compares_but_cannot_store()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Golden(app)); // shared, borrowed by A alone

        // Version 1 is never a row's xmin.
        Assert.Equal((HttpStatusCode.Conflict, "realestate.loan_conflict", (string?)null), await RefusalAsync(await PutAsync(app, id, BankTable, 1)));
        Assert.Equal((HttpStatusCode.Conflict, "realestate.loan_conflict", (string?)null),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Table(id)}?version=1")));
        Assert.Equal(HttpStatusCode.OK, (await PreviewRawAsync(app, id, new { text = BankTable }, app.AdultB)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(app, id, BankTable, await ContractVersionAsync(app, id), app.AdultB)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await app.RawAsync(app.AdultB, HttpMethod.Patch, $"{Loans}/{id}", new { scheduleSource = "computed" })).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await DetailAsync(app, id)).GetProperty("bankSchedule").ValueKind);
    }

    [Fact]
    public async Task A_contract_the_stored_table_no_longer_fits_is_refused_at_bankSchedule()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Golden(app));
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(app, id, BankTable, await ContractVersionAsync(app, id))).StatusCode);
        var contract = Contract();
        contract["firstPaymentDate"] = "2020-11-20"; // the table's dates are no longer due dates
        contract["version"] = await ContractVersionAsync(app, id);

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.bank_schedule_invalid", "bankSchedule"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Loans}/{id}/contract", contract)));
    }

    /// <summary>Precision 2 and 4: a repayment inside the table's period would make its balance rise; a repayment route answers at amount.</summary>
    [Fact]
    public async Task A_repayment_the_stored_table_no_longer_fits_is_refused_at_amount()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Golden(app));
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(app, id, BankTable, await ContractVersionAsync(app, id))).StatusCode);

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.bank_schedule_invalid", "amount"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Loans}/{id}/repayments",
                new { onDate = "2020-12-01", amount = "1000", effect = "reduce_term" })));
    }

    /// <summary>Spec 4D2 §3: rows given as data are checked and stored as a pasted table is; the bank is the reference only when asked.</summary>
    [Fact]
    public async Task Rows_given_as_data_are_stored_as_a_pasted_table_and_may_become_the_reference()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Golden(app));
        var loan = new ItemId(id);
        IReadOnlyList<BankRow> rows =
        [
            new(new LocalDate(2020, 11, 15), 1159.92m, 583.33m, 576.59m, null, 199_423.41m),
            new(new LocalDate(2020, 12, 15), 1159.92m, 581.65m, 578.27m, null, 198_845.14m),
            new(new LocalDate(2021, 1, 15), 1159.92m, 579.96m, 579.96m, null, 198_265.18m),
        ];

        var (_, gap) = await app.InvokeAsync(app.AdultA, new ReplaceBankRowsCommand(loan, [rows[0], rows[2]], MakeReference: true));
        Assert.Equal(("realestate.bank_schedule_invalid", "rows[1].dueDate"), (gap!.Code, FieldErrors.Of(gap)![0].Path));
        Assert.Equal(JsonValueKind.Null, (await DetailAsync(app, id)).GetProperty("bankSchedule").ValueKind);

        var (_, kept) = await app.InvokeAsync(app.AdultA, new ReplaceBankRowsCommand(loan, rows, MakeReference: false));
        Assert.Null(kept);
        var stored = await DetailAsync(app, id);
        Assert.Equal(("computed", 3), (S(stored, "scheduleSource"), stored.GetProperty("bankSchedule").GetProperty("rows").GetArrayLength()));

        var (_, chosen) = await app.InvokeAsync(app.AdultA, new ReplaceBankRowsCommand(loan, rows, MakeReference: true));
        Assert.Null(chosen);
        Assert.Equal("bank", S(await DetailAsync(app, id), "scheduleSource"));

        // Not asking leaves the reference as it is: the bank stays.
        var (_, again) = await app.InvokeAsync(app.AdultA, new ReplaceBankRowsCommand(loan, rows, MakeReference: false));
        Assert.Null(again);
        Assert.Equal("bank", S(await DetailAsync(app, id), "scheduleSource"));
    }
}
