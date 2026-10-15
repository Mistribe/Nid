using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.Subscriptions.IntegrationTests.Export;
using Nid.Subscriptions.IntegrationTests.RealEstate;
using Nid.TestSupport;
using static Nid.Subscriptions.IntegrationTests.Import.EnvelopesImportTests;
using static Nid.Subscriptions.IntegrationTests.Import.EstateFile;
using static Nid.Subscriptions.IntegrationTests.Import.PropertiesImportTests;

namespace Nid.Subscriptions.IntegrationTests.Import;

/// <summary>Spec 3F1 §2–§4: the loan sections, through the real loan and property API.</summary>
[Collection("postgres")]
public class LoanImportTests(PostgresFixture fixture)
{
    private static async Task<List<JsonElement>> LoansAsync(NidApp app, PersonId reader)
        => (await JsonAsync(app, reader, "/api/v1/realestate/loans?scope=me")).GetProperty("items").EnumerateArray().ToList();

    private static async Task<Guid> LoanIdAsync(NidApp app, string name)
        => (await LoansAsync(app, app.AdultA)).Single(l => S(l, "name") == name).GetProperty("id").GetGuid();

    private static async Task<JsonElement> LoanAsync(NidApp app, string name)
        => await JsonAsync(app, app.AdultA, $"/api/v1/realestate/loans/{await LoanIdAsync(app, name)}");

    private static IEnumerable<(int Seq, string Mode, string Rate)> PhasesOf(JsonElement loan)
        => loan.GetProperty("contract").GetProperty("phases").EnumerateArray().Select(p => (p.GetProperty("seq").GetInt32(), S(p, "mode"), S(p, "annualRate")));

    /// <summary>Review Focus 4: the phases are listed out of order, one rate is the JSON number 0 — the seq decides, a zero rate is a rate.</summary>
    [Fact]
    public async Task Loans_and_their_parts_are_created_and_linked_to_their_property()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var committed = await CommitAsync(new ImportClient(app), app, app.AdultA, Loans());

        Assert.Equal(new CountsResponse(2, 0, 0), committed.Report!.Sections["loans"]);
        Assert.Equal(new CountsResponse(3, 0, 0), committed.Report.Sections["loan_phases"]);
        Assert.Equal(new CountsResponse(1, 0, 0), committed.Report.Sections["loan_insurances"]);
        Assert.Equal(new CountsResponse(1, 0, 0), committed.Report.Sections["loan_fees"]);
        Assert.Equal(new CountsResponse(1, 0, 0), committed.Report.Sections["loan_events"]);
        var main = await LoanAsync(app, "Prêt principal");
        Assert.Equal(("Banque", "EUR", "shared", "Maison"), (S(main, "lender"), S(main, "currency"), S(main, "visibility"), S(main, "propertyName")));
        var contract = main.GetProperty("contract");
        Assert.Equal(("200000.00", "2020-10-15", 240), (S(contract, "principal"), S(contract, "disbursedOn"), contract.GetProperty("totalPayments").GetInt32()));
        Assert.Equal([(1, "annuity", "0.035")], PhasesOf(main));
        Assert.Equal(("rate_on_initial", "0.003"), (S(contract.GetProperty("insurances")[0], "mode"), S(contract.GetProperty("insurances")[0], "annualRate")));
        Assert.Equal(("file", "1000.00"), (S(main.GetProperty("fees")[0], "kind"), S(main.GetProperty("fees")[0], "amount")));
        var repayment = main.GetProperty("repayments")[0];
        Assert.Equal(("2023-03-15", "10000.00", "reduce_term"), (S(repayment, "onDate"), S(repayment, "amount"), S(repayment, "effect")));
        Assert.Equal([(1, "deferred_total", "0"), (2, "annuity", "0")], PhasesOf(await LoanAsync(app, "Prêt à taux zéro")));
        Assert.Equal(2, (await PropertyAsync(app, "Maison")).GetProperty("loans").GetArrayLength());
        Assert.Contains(await LoansAsync(app, app.AdultB), l => S(l, "name") == "Prêt principal");
    }

    [Fact]
    public async Task The_same_file_again_is_unchanged()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, Loans());

        var again = await CommitAsync(client, app, app.AdultA, Loans());

        Assert.Equal(new CountsResponse(0, 2, 0), again.Report!.Sections["loans"]);
        Assert.Equal(new CountsResponse(0, 3, 0), again.Report.Sections["loan_phases"]);
        foreach (var section in new[] { "loan_insurances", "loan_fees", "loan_events" })
            Assert.Equal(new CountsResponse(0, 1, 0), again.Report.Sections[section]);
        Assert.Empty(again.Report.Issues);
        Assert.Equal(2, (await LoansAsync(app, app.AdultA)).Count);
    }

    [Fact]
    public async Task A_changed_phase_is_a_conflict_and_replace_rewrites_the_contract_keeping_every_file_id()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, Loans());
        var lower = Loans(phases: List(ZeroAnnuity, MainRate.Replace("0.035", "0.032"), ZeroDeferred));

        var validated = await ValidateAsync(client, app, app.AdultA, lower);

        Assert.Equal("invalid", validated.State);
        Assert.Equal([(ImportCodes.Conflict, "loan_phases[1]")], ErrorsOf(validated));
        Assert.Equal(new CountsResponse(0, 2, 1), validated.Report!.Sections["loan_phases"]);
        await client.CancelRawAsync(app.AdultA, validated.Id);

        var replaced = await CommitAsync(client, app, app.AdultA, lower, replace: true);

        Assert.Equal(new CountsResponse(0, 2, 0, 1), replaced.Report!.Sections["loan_phases"]);
        Assert.Equal(new CountsResponse(0, 2, 0), replaced.Report.Sections["loans"]);
        var main = await LoanAsync(app, "Prêt principal");
        Assert.Equal([(1, "annuity", "0.032")], PhasesOf(main));
        Assert.Equal((1, 1, 1), (main.GetProperty("contract").GetProperty("insurances").GetArrayLength(),
            main.GetProperty("fees").GetArrayLength(), main.GetProperty("repayments").GetArrayLength()));

        // The rewritten contract kept the insurance's file id and gave the phase its new hash: the same file again is unchanged.
        var again = await CommitAsync(client, app, app.AdultA, lower);
        Assert.Equal(new CountsResponse(0, 3, 0), again.Report!.Sections["loan_phases"]);
        Assert.Equal(new CountsResponse(0, 1, 0), again.Report.Sections["loan_insurances"]);
    }

    /// <summary>Review Focus 1: the app's contract PUT keeps each phase's file id by its number and each insurance's by its rank.</summary>
    [Fact]
    public async Task A_phase_edited_in_the_app_keeps_its_file_id_and_the_file_reimports_unchanged()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, Loans());
        var main = await LoanIdAsync(app, "Prêt principal");
        var body = LoanApi.Contract("200000", LoanApi.Phase(annualRate: "0.03"));
        body["insurances"] = new[] { new { mode = "rate_on_initial", annualRate = "0.003", startsOn = "2020-11-15" } };
        body["version"] = await LoanApi.ContractVersionAsync(app, main);
        var put = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{LoanApi.Loans}/{main}/contract", body);
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());

        var again = await CommitAsync(client, app, app.AdultA, Loans());

        Assert.Equal(new CountsResponse(0, 3, 0), again.Report!.Sections["loan_phases"]);
        Assert.Equal(new CountsResponse(0, 1, 0), again.Report.Sections["loan_insurances"]);
        Assert.Equal([(1, "annuity", "0.03")], PhasesOf(await LoanAsync(app, "Prêt principal")));
    }

    /// <summary>
    /// Review Focus 1: deleting the first of two imported insurances in the app frees its file id — the second keeps its own, so the
    /// same file again recreates the first instead of doubling the second.
    /// </summary>
    [Fact]
    public async Task An_insurance_deleted_in_the_app_is_recreated_by_the_file_without_doubling_the_other()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var second = """{"id":"li-2","loan":"loan-main","mode":"rate_on_initial","annual_rate":"0.001","starts_on":"2021-11-15"}""";
        var file = Loans(insurances: List(Insurance, second));
        await CommitAsync(client, app, app.AdultA, file);
        var main = await LoanIdAsync(app, "Prêt principal");
        var body = LoanApi.Contract();
        body["insurances"] = new[] { new { mode = "rate_on_initial", annualRate = "0.001", startsOn = "2021-11-15" } };
        body["version"] = await LoanApi.ContractVersionAsync(app, main);
        var put = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{LoanApi.Loans}/{main}/contract", body);
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());

        var again = await CommitAsync(client, app, app.AdultA, file);

        Assert.Equal(new CountsResponse(1, 1, 0), again.Report!.Sections["loan_insurances"]);
        var insurances = (await LoanAsync(app, "Prêt principal")).GetProperty("contract").GetProperty("insurances").EnumerateArray()
            .Select(i => (S(i, "annualRate"), S(i, "startsOn")));
        Assert.Equal([("0.003", "2020-11-15"), ("0.001", "2021-11-15")], insurances);
    }

    [Fact]
    public async Task Every_rule_is_reported_at_its_field()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);

        // Rules local to an entity: the receipt of its batch reports them.
        var session = await client.StartAsync(app.AdultA, Adults(app));
        var loans = await client.PutAsync(app.AdultA, session.Id, "loans", 0, List(
            MainLoan.Replace("\"visibility\":\"shared\"", "\"visibility\":\"shared\",\"schedule_source\":\"spreadsheet\""),
            MainLoan.Replace("loan-main", "l1").Replace("\"visibility\":\"shared\"", "\"visibility\":\"shared\",\"rounding_mode\":\"up\""),
            MainLoan.Replace("loan-main", "l2").Replace("\"amount\":\"200000.00\"", "\"amount\":\"0.001\""),
            MainLoan.Replace("loan-main", "l3").Replace("\"name\":\"Prêt principal\"", "\"name\":\"  \""),
            MainLoan.Replace("loan-main", "l4").Replace("\"currency\":\"EUR\",\"principal\"", "\"currency\":\"USD\",\"principal\"")));
        var phases = await client.PutAsync(app.AdultA, session.Id, "loan_phases", 0, List(
            MainRate.Replace("annuity", "balloon"),
            MainRate.Replace("lp-1", "p1").Replace("0.035", "0.0350001")));
        var fees = await client.PutAsync(app.AdultA, session.Id, "loan_fees", 0, List(
            FileFee.Replace("\"kind\":\"file\"", "\"kind\":\"stamp\""),
            FileFee.Replace("fee-file", "f1").Replace("1000.00", "0")));
        var events = await client.PutAsync(app.AdultA, session.Id, "loan_events", 0, List(Repayment.Replace("reduce_term", "skip")));
        await client.CancelRawAsync(app.AdultA, session.Id);

        Assert.Equal(
            [
                (ImportCodes.InvalidValue, "loans[0].schedule_source"),
                (ImportCodes.InvalidValue, "loans[1].rounding_mode"),
                (LoanCodes.InvalidLoan, "loans[2].principal.amount"),
                (LoanCodes.InvalidLoan, "loans[3].name"),
                (CoreFactCodes.CurrencyMismatch, "loans[4].principal.currency"),
            ],
            loans.Issues.Select(i => (i.Code, i.Path)));
        Assert.Equal([(ImportCodes.InvalidValue, "loan_phases[0].mode"), (LoanCodes.InvalidLoan, "loan_phases[1].annual_rate")],
            phases.Issues.Select(i => (i.Code, i.Path)));
        Assert.Equal([(ImportCodes.InvalidValue, "loan_fees[0].kind"), (LoanCodes.InvalidLoan, "loan_fees[1].amount.amount")],
            fees.Issues.Select(i => (i.Code, i.Path)));
        Assert.Equal([(ImportCodes.InvalidValue, "loan_events[0].effect")], events.Issues.Select(i => (i.Code, i.Path)));

        // Rules over the whole loan or the whole file: the validation reports them, at the entity that brings the fault.
        var validated = await ValidateAsync(client, app, app.AdultA, Loans(
            loans: List(MainLoan, ZeroLoan, MainLoan.Replace("loan-main", "loan-x").Replace("prop-home", "prop-nobody")),
            phases: List(ZeroAnnuity, MainRate, ZeroDeferred, MainRate.Replace("lp-1", "lp-9").Replace("\"seq\":1", "\"seq\":3"),
                MainRate.Replace("lp-1", "lp-x").Replace("loan-main", "loan-nobody")),
            insurances: "[]", fees: "[]",
            events: List(Repayment.Replace("ev-1", "ev-big").Replace("loan-main", "loan-ptz").Replace("2023-03-15", "2021-01-15").Replace("10000.00", "50000.00"))));

        Assert.Equal("invalid", validated.State);
        Assert.Equal(
            new[]
            {
                (ImportCodes.UnresolvedReference, "loans[2].property"),
                (ImportCodes.UnresolvedReference, "loan_phases[4].loan"),
                (LoanCodes.InvalidLoan, "loan_phases[3].seq"),
                (LoanCodes.EventAboveBalance, "loan_events[0].amount.amount"),
            }.Order(),
            ErrorsOf(validated).Order());
        await client.CancelRawAsync(app.AdultA, validated.Id);

        // A loan is imported by one of its borrowers; its parts are not reported again.
        var outside = await ValidateAsync(client, app, app.AdultA, Loans(loans: List(MainLoan.Replace(
            "[{\"person\":\"adult-1\",\"share_bp\":5000},{\"person\":\"adult-2\",\"share_bp\":5000}]", "[{\"person\":\"adult-2\",\"share_bp\":10000}]"), ZeroLoan)));

        Assert.Equal([(ImportCodes.NotPermitted, "loans[0]")], ErrorsOf(outside));
    }

    /// <summary>Review Focus 3: batches go out in any order (import-format.md §11.2); the references are resolved at validation.</summary>
    [Fact]
    public async Task A_loan_sent_before_the_property_it_names_finds_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var reordered = Loans().OrderBy(s => s.Key.StartsWith("loan", StringComparison.Ordinal) ? 0 : 1).ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal("loans", reordered.Keys.First());

        var committed = await CommitAsync(new ImportClient(app), app, app.AdultA, reordered);

        Assert.Equal(new CountsResponse(2, 0, 0), committed.Report!.Sections["loans"]);
        Assert.Equal("Maison", S(await LoanAsync(app, "Prêt principal"), "propertyName"));
    }

    /// <summary>Review Focus 5: each amount in its own item's currency; the property converts its loan and says so.</summary>
    [Fact]
    public async Task A_property_in_dollars_takes_its_facts_in_dollars_and_its_loan_its_amounts_in_euros()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IFxRateStore>()
                .WriteAsync([new FxRate(NidApp.GoldenCaseToday, Currency.Eur, 0.9m, FxRateSource.Ecb)], default); // 1 USD = 0.90 EUR
        var client = new ImportClient(app);
        var condo = Flat.Replace("prop-flat", "prop-condo").Replace("\"name\":\"Studio\"", "\"name\":\"Condo\"").Replace("EUR", "USD");
        static string Expense(string id, string currency)
            => $$"""{"id":"{{id}}","item":"prop-condo","occurred_on":"2024-05-10","kind":"expense","amount":{"amount":"900","currency":"{{currency}}"},"category":"insurance"}""";
        var file = Loans(loans: List(MainLoan.Replace("prop-home", "prop-condo")), phases: List(MainRate), insurances: "[]",
            fees: List(FileFee.Replace("\"amount\":\"1000.00\",\"currency\":\"EUR\"", "\"amount\":\"1000.00\",\"currency\":\"USD\"")), events: "[]");
        file["properties"] = List(condo);
        file["property_recurring_costs"] = "[]";
        file["tenancies"] = "[]";
        file["flows"] = List(Expense("f-usd", "USD"), Expense("f-eur", "EUR"));

        var refused = await ValidateAsync(client, app, app.AdultA, file);

        Assert.Equal([(CoreFactCodes.CurrencyMismatch, "loan_fees[0].amount.currency"), (CoreFactCodes.CurrencyMismatch, "flows[1].amount.currency")],
            ErrorsOf(refused));
        await client.CancelRawAsync(app.AdultA, refused.Id);

        file["loan_fees"] = "[]";
        file["flows"] = List(Expense("f-usd", "USD"));
        await CommitAsync(client, app, app.AdultA, file);

        var detail = await PropertyAsync(app, "Condo");
        Assert.Equal(("USD", "900.00"), (S(detail, "currency"), S(detail.GetProperty("flows")[0], "amount")));
        Assert.Equal("EUR", S(Assert.Single(detail.GetProperty("loans").EnumerateArray()), "currency"));
        Assert.Contains(detail.GetProperty("warnings").EnumerateArray(), w => S(w, "code") == "loan_other_currency");
    }

    /// <summary>Review Focus 2: a borrower and owner mismatch is the household's choice, warned on the property, and stays private.</summary>
    [Fact]
    public async Task A_private_loan_on_a_shared_property_imports_with_its_warning_and_stays_out_of_the_other_adults_export()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = MainLoan.Replace("loan-main", "loan-mine").Replace("\"name\":\"Prêt principal\"", "\"name\":\"Secret loan\"")
            .Replace("[{\"person\":\"adult-1\",\"share_bp\":5000},{\"person\":\"adult-2\",\"share_bp\":5000}],\"visibility\":\"shared\"",
                "[{\"person\":\"adult-1\",\"share_bp\":10000}],\"visibility\":\"private\"");

        await CommitAsync(new ImportClient(app), app, app.AdultA,
            Loans(loans: List(mine), phases: List(MainRate.Replace("loan-main", "loan-mine")), insurances: "[]", fees: "[]", events: "[]"));

        Assert.Contains((await PropertyAsync(app, "Maison")).GetProperty("warnings").EnumerateArray(), w => S(w, "code") == "visibility_mismatch");
        var forA = await new ExportClient(app).ExportAllAsync(app.AdultA);
        Assert.Equal("prop-home", S(Assert.Single(forA["loans"]), "property"));
        var forB = await new ExportClient(app).ExportAllAsync(app.AdultB);
        Assert.Contains(forB["properties"], p => S(p, "id") == "prop-home");
        Assert.Empty(forB["loans"]);
        Assert.Empty(forB["loan_phases"]);
        var text = JsonSerializer.Serialize(forB);
        Assert.DoesNotContain("Secret loan", text);
        Assert.DoesNotContain("loan-mine", text);
    }

    /// <summary>Spec 3F1 §4: a loan linked to a property the reader cannot read is exported without its property, which nothing reveals.</summary>
    [Fact]
    public async Task A_loan_linked_to_a_property_the_reader_cannot_read_is_exported_without_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await PropertyApi.NewPropertyAsync(app, PropertyApi.Home(app, "Hidden house", "private", app.AdultB), app.AdultB);
        var body = LoanApi.Golden(app, "Joint loan", "shared", app.AdultA, app.AdultB);
        body["propertyId"] = hidden;
        await LoanApi.NewAsync(app, body, app.AdultB);

        var forA = await new ExportClient(app).ExportAllAsync(app.AdultA);

        var loan = Assert.Single(forA["loans"]);
        Assert.Equal(("Joint loan", JsonValueKind.Null), (S(loan, "name"), loan.GetProperty("property").ValueKind));
        Assert.Empty(forA["properties"]);
        var text = JsonSerializer.Serialize(forA);
        Assert.DoesNotContain("Hidden house", text);
        Assert.DoesNotContain(hidden.ToString(), text);

        // Adult-2 reads both: the link is in their file.
        var forB = await new ExportClient(app).ExportAllAsync(app.AdultB);
        Assert.Equal(S(Assert.Single(forB["properties"]), "id"), S(Assert.Single(forB["loans"]), "property"));
    }

    [Fact]
    public async Task The_export_writes_loans_and_their_parts_with_their_defaults()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await CommitAsync(new ImportClient(app), app, app.AdultA, Loans());

        var export = await new ExportClient(app).ExportAllAsync(app.AdultA);

        Assert.Equal(["loan-main", "loan-ptz"], export["loans"].Select(l => S(l, "id"))); // by name, ordinal: « Prêt p… » before « Prêt à… »
        var main = export["loans"][0];
        Assert.Equal(("prop-home", "computed", "half_up", "full"), (S(main, "property"), S(main, "schedule_source"), S(main, "rounding_mode"), S(main, "first_period_mode")));
        Assert.Equal(("200000.00", 240, "Banque"), (S(main.GetProperty("principal"), "amount"), main.GetProperty("total_payments").GetInt32(), S(main, "lender")));
        Assert.Equal([("lp-1", "loan-main", 1, "0.035"), ("lp-2", "loan-ptz", 1, "0"), ("lp-3", "loan-ptz", 2, "0")],
            export["loan_phases"].Select(p => (S(p, "id"), S(p, "loan"), p.GetProperty("seq").GetInt32(), S(p, "annual_rate"))));
        Assert.Equal(("li-1", "0.003"), (S(export["loan_insurances"][0], "id"), S(export["loan_insurances"][0], "annual_rate")));
        Assert.Equal(("fee-file", "1000.00"), (S(export["loan_fees"][0], "id"), S(export["loan_fees"][0].GetProperty("amount"), "amount")));
        Assert.Equal(("ev-1", "reduce_term", "2023-03-15"), (S(export["loan_events"][0], "id"), S(export["loan_events"][0], "effect"), S(export["loan_events"][0], "on_date")));
    }

    private static JsonElement TableOf(JsonElement loan) => loan.GetProperty("bankSchedule");

    /// <summary>Spec 3F2 §3: the rows arrive in any order; the loan takes the bank as the reference; the same file again is unchanged.</summary>
    [Fact]
    public async Task A_bank_table_imports_with_its_loan_and_the_bank_as_reference()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var file = Loans(loans: List(BankLoan, ZeroLoan), bank: List(Bank3, Bank1, Bank2));

        var committed = await CommitAsync(client, app, app.AdultA, file);

        Assert.Equal(new CountsResponse(3, 0, 0), committed.Report!.Sections["loan_bank_schedule"]);
        var main = await LoanAsync(app, "Prêt principal");
        Assert.Equal(("bank", "2020-11-15", "2021-01-15"), (S(main, "scheduleSource"), S(TableOf(main), "firstDueOn"), S(TableOf(main), "lastDueOn")));
        Assert.Equal("198265.18", S(TableOf(main).GetProperty("rows")[2], "crd"));
        Assert.Equal("computed", S(await LoanAsync(app, "Prêt à taux zéro"), "scheduleSource"));

        var again = await CommitAsync(client, app, app.AdultA, file);

        Assert.Equal(new CountsResponse(0, 3, 0), again.Report!.Sections["loan_bank_schedule"]);
    }

    /// <summary>Review Focus 5 among them: the payment with the insurance (50 a month on this loan) and no insurance member.</summary>
    [Fact]
    public async Task The_bank_without_rows_and_a_broken_table_are_refused_at_their_entity()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);

        var session = await client.StartAsync(app.AdultA, Adults(app));
        var batch = await client.PutAsync(app.AdultA, session.Id, "loan_bank_schedule", 0, List(Bank1.Replace("2020-11-15", "15/11/2020")));
        await client.CancelRawAsync(app.AdultA, session.Id);
        Assert.Equal([(ImportCodes.InvalidValue, "loan_bank_schedule[0].due_date")], batch.Issues.Select(i => (i.Code, i.Path)));

        var missing = await ValidateAsync(client, app, app.AdultA, Loans(loans: List(BankLoan, ZeroLoan)));
        Assert.Equal([(LoanCodes.BankScheduleMissing, "loans[0].schedule_source")], ErrorsOf(missing));
        await client.CancelRawAsync(app.AdultA, missing.Id);

        var offCalendar = await ValidateAsync(client, app, app.AdultA, Loans(bank: List(Bank1, Bank2.Replace("2020-12-15", "2020-12-16"), Bank3)));
        Assert.Equal([(LoanCodes.BankScheduleInvalid, "loan_bank_schedule[1].due_date")], ErrorsOf(offCalendar));
        await client.CancelRawAsync(app.AdultA, offCalendar.Id);

        var insured = await ValidateAsync(client, app, app.AdultA, Loans(bank: List(Bank1.Replace("\"payment\":\"1159.92\"", "\"payment\":\"1209.92\""))));
        Assert.Equal([(LoanCodes.BankScheduleInvalid, "loan_bank_schedule[0].payment")], ErrorsOf(insured));
    }

    /// <summary>Spec 3F2 precision 5: a table replaced in the app keeps each row's file id by its date — the file re-imports unchanged, the app's rows stay.</summary>
    [Fact]
    public async Task A_table_replaced_in_the_app_keeps_its_file_ids_and_the_file_reimports_unchanged()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var file = Loans(bank: List(Bank1, Bank2, Bank3));
        await CommitAsync(client, app, app.AdultA, file);
        var id = await LoanIdAsync(app, "Prêt principal");
        var version = (await LoanAsync(app, "Prêt principal")).GetProperty("contract").GetProperty("version").GetUInt32();
        var put = await app.RawAsync(app.AdultA, HttpMethod.Put, $"/api/v1/realestate/loans/{id}/bank-schedule",
            new { text = LoanApi.BankTable.Replace("198 265,18", "197 265,18"), version });
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());

        var again = await CommitAsync(client, app, app.AdultA, file);

        Assert.Equal(new CountsResponse(0, 3, 0), again.Report!.Sections["loan_bank_schedule"]);
        Assert.Equal("197265.18", S(TableOf(await LoanAsync(app, "Prêt principal")).GetProperty("rows")[2], "crd"));
    }

    /// <summary>import-format.md §12bis: rows at the dates of a table typed in the app are refused on a first import of rows, not merged.</summary>
    [Fact]
    public async Task Rows_at_the_dates_of_a_table_stored_in_the_app_are_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, Loans(loans: List(MainLoan, ZeroLoan)));
        var id = await LoanIdAsync(app, "Prêt principal");
        var version = (await LoanAsync(app, "Prêt principal")).GetProperty("contract").GetProperty("version").GetUInt32();
        var put = await app.RawAsync(app.AdultA, HttpMethod.Put, $"/api/v1/realestate/loans/{id}/bank-schedule", new { text = LoanApi.BankTable, version });
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());

        var clash = await ValidateAsync(client, app, app.AdultA, Loans(loans: List(BankLoan, ZeroLoan), bank: List(Bank1, Bank2, Bank3)), replace: true);

        Assert.Equal([(LoanCodes.BankScheduleInvalid, "loan_bank_schedule[0].due_date")], ErrorsOf(clash));
    }

    /// <summary>
    /// Spec 3F2 precision 4: a stored table always fits its loan. A file that no longer brings the table but moves the loan's due
    /// dates is checked against the stored rows, and refused at the loan, even with replace: nothing is written.
    /// </summary>
    [Fact]
    public async Task A_contract_change_the_stored_table_no_longer_fits_is_refused_and_nothing_is_written()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, Loans(loans: List(BankLoan, ZeroLoan), bank: List(Bank1, Bank2, Bank3)));

        var moved = await ValidateAsync(client, app, app.AdultA,
            Loans(loans: List(BankLoan.Replace("\"first_payment_date\":\"2020-11-15\"", "\"first_payment_date\":\"2020-11-16\""), ZeroLoan)), replace: true);

        Assert.Equal([(LoanCodes.BankScheduleInvalid, "loans[0]")], ErrorsOf(moved));
        await client.CancelRawAsync(app.AdultA, moved.Id);
        var main = await LoanAsync(app, "Prêt principal");
        Assert.Equal(("2020-11-15", "bank", 3), (S(main.GetProperty("contract"), "firstPaymentDate"), S(main, "scheduleSource"),
            TableOf(main).GetProperty("rows").GetArrayLength()));
    }

    [Fact]
    public async Task The_export_writes_the_bank_table_and_the_reference()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await CommitAsync(new ImportClient(app), app, app.AdultA, Loans(loans: List(BankLoan, ZeroLoan), bank: List(Bank2, Bank1, Bank3)));

        var export = await new ExportClient(app).ExportAllAsync(app.AdultA);

        Assert.Equal(("bank", "computed"), (S(export["loans"][0], "schedule_source"), S(export["loans"][1], "schedule_source")));
        Assert.Equal(
            [("lbs-1", "2020-11-15", "1159.92", "199423.41"), ("lbs-2", "2020-12-15", "1159.92", "198845.14"), ("lbs-3", "2021-01-15", "1159.92", "198265.18")],
            export["loan_bank_schedule"].Select(r => (S(r, "id"), S(r, "due_date"), S(r, "payment"), S(r, "crd"))));
        Assert.Equal((JsonValueKind.Null, "loan-main"), (export["loan_bank_schedule"][0].GetProperty("insurance").ValueKind, S(export["loan_bank_schedule"][0], "loan")));
    }
}
