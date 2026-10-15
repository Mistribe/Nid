using System.Net;
using Microsoft.EntityFrameworkCore;
using Nid.TestSupport;
using static Nid.Subscriptions.IntegrationTests.RealEstate.LoanApi;

namespace Nid.Subscriptions.IntegrationTests.RealEstate;

/// <summary>Spec 3A2 §5: the reads, the creation, the PATCH, the contract, and the 404s.</summary>
[Collection("postgres")]
public class LoanEndpointTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_loan_is_created_and_read_back_at_its_60th_payment()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Golden(app, visibility: "private"));

        var row = Assert.Single((await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Loans}?scope=me"))).GetProperty("items").EnumerateArray());
        Assert.Equal(id, row.GetProperty("id").GetGuid());
        Assert.Equal(("Prêt maison", "Banque", "EUR"), (S(row, "name"), S(row, "lender"), S(row, "currency")));
        Assert.Equal("private", S(row, "visibility"));
        Assert.Equal(("200000.00", "162253.12"), (S(row, "principal"), S(row, "remainingBalance")));
        Assert.Equal(("2025-11-15", "1159.92", "0.035", "2040-10-15"),
            (S(row, "nextPaymentOn"), S(row, "nextPaymentTotal"), S(row, "currentRate"), S(row, "endDate")));
        Assert.True(row.GetProperty("canWrite").GetBoolean());
        Assert.False(row.GetProperty("ended").GetBoolean());
        Assert.Empty((await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Loans}?scope=household"))).GetProperty("items").EnumerateArray());

        var detail = await DetailAsync(app, id);
        var contract = detail.GetProperty("contract");
        Assert.Equal(("200000.00", "2020-10-15", "half_up", "full"),
            (S(contract, "principal"), S(contract, "disbursedOn"), S(contract, "rounding"), S(contract, "firstPeriod")));
        var phase = Assert.Single(contract.GetProperty("phases").EnumerateArray());
        Assert.Equal(("annuity", "0.035", "fixed"), (S(phase, "mode"), S(phase, "annualRate"), S(phase, "rateType")));
        Assert.Equal("private", S(detail, "visibility"));
        Assert.Equal(app.AdultA.Value, Only(detail, "borrowers").GetProperty("personId").GetGuid());
        var figures = detail.GetProperty("figures");
        Assert.Equal(("162253.12", "31848.32", 180), (S(figures, "balance"), S(figures, "interestPaid"), figures.GetProperty("paymentsLeft").GetInt32()));
        Assert.Empty(detail.GetProperty("warnings").EnumerateArray());

        var rows = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Loans}/{id}/schedule"))).GetProperty("rows");
        Assert.Equal(240, rows.GetArrayLength());
        Assert.Equal(("1159.92", "162253.12"), (S(rows[0], "payment"), S(rows[59], "balance")));

        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var item = await core.Items.SingleAsync(i => i.Id == id);
        Assert.Equal(("realestate", "loan", "EUR"), (item.Module, item.Kind, item.Currency!.Trim()));
    }

    [Fact]
    public async Task Each_creation_refusal_answers_at_the_bodys_own_path() // Review Focus #3 for the French comma
    {
        await using var app = await NidApp.StartAsync(fixture);
        const HttpStatusCode Unprocessable = HttpStatusCode.UnprocessableEntity;
        var cases = new (string Why, Action<Dictionary<string, object?>> Change, HttpStatusCode Status, string? Code, string? Path)[]
        {
            ("no visibility", b => b.Remove("visibility"), Unprocessable, "request.visibility", "visibility"),
            ("no borrower", b => b.Remove("borrowers"), Unprocessable, "request.required", "borrowers"),
            ("a lowercase currency", b => b["currency"] = "eur", Unprocessable, "core.unknown_currency", "currency"),
            ("a French decimal comma", b => b["principal"] = "200000,50", Unprocessable, "request.amount", "principal"),
            ("a third decimal", b => b["principal"] = "200000.005", Unprocessable, "realestate.invalid_loan", "principal"),
            ("a trillion", b => b["principal"] = "1000000000000", Unprocessable, "realestate.invalid_loan", "principal"),
            ("no payment", b => b["totalPayments"] = 0, Unprocessable, "realestate.invalid_loan", "totalPayments"),
            ("an unknown rounding", b => b["rounding"] = "bankers", Unprocessable, "realestate.invalid_loan", "rounding"),
            ("a seventh rate decimal", b => b["phases"] = new[] { Phase(annualRate: "0.0350001") }, Unprocessable, "realestate.invalid_loan", "phases[0].annualRate"),
            ("a rate of 120 %", b => b["phases"] = new[] { Phase(annualRate: "1.2") }, Unprocessable, "realestate.invalid_loan", "phases[0].annualRate"),
            ("an open phase before the last", b => b["phases"] = new[] { Phase(1), Phase(2) }, Unprocessable, "realestate.invalid_loan", "phases[0].payments"),
            ("an imposed payment below the interest", b => b["phases"] = new[] { Phase(paymentOverride: "100") }, Unprocessable,
                "realestate.payment_below_interest", "phases[0].paymentOverride"),
            ("an insurance ending as it starts", b => b["insurances"] = new[]
                { new { mode = "fixed_monthly", monthlyAmount = "20", startsOn = "2020-11-15", endsOn = "2020-11-15" } },
                Unprocessable, "realestate.invalid_loan", "insurances[0].endsOn"),
            ("no rounding", b => b.Remove("rounding"), Unprocessable, "request.required", "rounding"),
            ("a phase without its mode", b => b["phases"] = new[] { new { seq = 1, annualRate = "0.035", rateType = "fixed" } },
                Unprocessable, "request.required", "phases[0].mode"),
            ("a fixed insurance sent a rate of 100 %", b => b["insurances"] = new[]
                { new { mode = "fixed_monthly", monthlyAmount = "20", annualRate = "1", startsOn = "2020-11-15" } },
                Unprocessable, "realestate.invalid_loan", "insurances[0].annualRate"),
            ("a rated insurance sent a zero monthly amount", b => b["insurances"] = new[]
                { new { mode = "rate_on_initial", monthlyAmount = "0", annualRate = "0.003", startsOn = "2020-11-15" } },
                Unprocessable, "realestate.invalid_loan", "insurances[0].monthlyAmount"),
            ("a disbursement in 1899", b => b["disbursedOn"] = "1899-12-31", Unprocessable, "realestate.invalid_loan", "disbursedOn"),
            ("a first payment in 2201", b => b["firstPaymentDate"] = "2201-01-15", Unprocessable, "realestate.invalid_loan", "firstPaymentDate"),
            ("an insurance ending in 2201", b => b["insurances"] = new[]
                { new { mode = "fixed_monthly", monthlyAmount = "20", startsOn = "2020-11-15", endsOn = "2201-01-01" } },
                Unprocessable, "realestate.invalid_loan", "insurances[0].endsOn"),
            ("21 insurances", b => b["insurances"] = Enumerable.Range(0, 21)
                .Select(_ => new { mode = "fixed_monthly", monthlyAmount = "1", startsOn = "2020-11-15" }).ToArray(),
                Unprocessable, "realestate.invalid_loan", "insurances"),
            ("amounts that outgrow decimal", b =>
                {
                    b["principal"] = "900000000000";
                    b["totalPayments"] = 600;
                    b["phases"] = new[] { Phase(mode: "deferred_total", annualRate: "0.99") };
                }, Unprocessable, "realestate.invalid_loan", "principal"),
            ("shares that do not add up", b => b["borrowers"] = new[] { new { personId = app.AdultA.Value, shareBp = 6_000 } },
                Unprocessable, "core.share_sum", "borrowers"),
            ("a private loan with two borrowers", b =>
                {
                    b["visibility"] = "private";
                    b["borrowers"] = new[] { new { personId = app.AdultA.Value, shareBp = 5_000 }, new { personId = app.AdultB.Value, shareBp = 5_000 } };
                }, Unprocessable, "core.private_single_owner", "borrowers"),
            ("the other adult's loan alone", b => b["borrowers"] = new[] { new { personId = app.AdultB.Value, shareBp = 10_000 } },
                HttpStatusCode.Forbidden, null, null),
        };

        foreach (var (why, change, status, code, path) in cases)
        {
            var body = Golden(app);
            change(body);
            var (gotStatus, gotCode, gotPath) = await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Loans, body));
            Assert.True(gotStatus == status && (code is null || (gotCode, gotPath) == (code, path)),
                $"{why}: expected {status} {code} at {path}, got {gotStatus} {gotCode} at {gotPath}");
        }

        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.Equal(0, await core.Items.CountAsync(i => i.Module == "realestate"));
    }

    [Fact]
    public async Task Ended_loans_are_listed_only_on_request()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var running = await NewAsync(app, Golden(app));
        var done = Golden(app, name: "Prêt auto");
        done["principal"] = "1200";
        done["disbursedOn"] = "2024-01-15";
        done["firstPaymentDate"] = "2024-02-15";
        done["totalPayments"] = 12;
        done["phases"] = new[] { Phase(annualRate: "0") };
        var ended = await NewAsync(app, done); // its last payment, 2025-01-15, is before 2025-10-15

        var current = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, Loans))).GetProperty("items");
        Assert.Equal([running], current.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()));
        var all = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Loans}?ended=true"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, all.Count);
        Assert.True(all.Single(r => r.GetProperty("id").GetGuid() == ended).GetProperty("ended").GetBoolean());
        Assert.True((await DetailAsync(app, ended)).GetProperty("ended").GetBoolean());
    }

    [Fact]
    public async Task The_schedule_totals_each_calendar_year() // spec 3B1 §4.1
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Golden(app));
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Loans}/{id}/repayments",
            new { onDate = "2022-03-15", amount = "5000", effect = "reduce_term" }));

        var schedule = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Loans}/{id}/schedule"));

        var years = schedule.GetProperty("years").EnumerateArray().ToList();
        // November and December 2020: the golden case's first two payments.
        Assert.Equal(2020, years[0].GetProperty("year").GetInt32());
        Assert.Equal(("2319.84", "1164.98", "1154.86", "0.00", "198845.14"),
            (S(years[0], "payment"), S(years[0], "interest"), S(years[0], "principal"), S(years[0], "insurance"), S(years[0], "balance")));
        Assert.Equal(("0.00", "5000.00"), (S(years[1], "earlyRepayment"), S(years[2], "earlyRepayment")));
        Assert.Equal("0.00", S(years[^1], "balance"));
        // Every row's principal, the repayment aside: the 5 000 is no row's principal.
        Assert.Equal("195000.00", S(schedule, "totalPrincipal"));
    }

    [Fact]
    public async Task Editing_changes_only_what_is_sent()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Golden(app));
        async Task PatchAsync(object body)
            => Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{id}", body)).StatusCode);

        await PatchAsync(new { lender = "Crédit Mutuel" });
        var detail = await DetailAsync(app, id);
        Assert.Equal(("Prêt maison", "Crédit Mutuel", "EUR"), (S(detail, "name"), S(detail, "lender"), S(detail, "currency")));

        // Borrowers and visibility are set at creation (spec §4): the body has no such members, so a client sending one changes nothing.
        await PatchAsync(new
        {
            clearLender = true, name = "Résidence", note = "taux renégocié", visibility = "private",
            borrowers = new[] { new { personId = app.AdultB.Value, shareBp = 10_000 } },
        });
        detail = await DetailAsync(app, id);
        Assert.Equal(("Résidence", (string?)null, "taux renégocié", "shared"), (S(detail, "name"), S(detail, "lender"), S(detail, "note"), S(detail, "visibility")));
        Assert.Equal(app.AdultA.Value, Only(detail, "borrowers").GetProperty("personId").GetGuid());
        Assert.Equal("200000.00", S(detail.GetProperty("contract"), "principal"));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_loan", "name"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{id}", new { name = " " })));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_loan", "lender"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{id}", new { lender = new string('x', 101) })));
    }

    [Fact]
    public async Task An_edit_of_lender_or_note_turns_a_pending_contract_put_into_a_conflict() // Review Focus #5
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Golden(app));
        var body = Contract("150000");

        // A rename is the Core item's: the contract's version does not move, the PUT read before it goes through.
        body["version"] = await ContractVersionAsync(app, id);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{id}", new { name = "Résidence" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Loans}/{id}/contract", body)).StatusCode);

        // A lender shares the loan's row with the contract (precision 5): the PUT read before it is a conflict, by design.
        body["version"] = await ContractVersionAsync(app, id);
        body["principal"] = "140000";
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Loans}/{id}", new { lender = "Crédit Mutuel" })).StatusCode);
        Assert.Equal((HttpStatusCode.Conflict, "realestate.loan_conflict", (string?)null),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Loans}/{id}/contract", body)));
        Assert.Equal("150000.00", S((await DetailAsync(app, id)).GetProperty("contract"), "principal"));

        // Read again, it goes through.
        body["version"] = await ContractVersionAsync(app, id);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Loans}/{id}/contract", body)).StatusCode);
        Assert.Equal("140000.00", S((await DetailAsync(app, id)).GetProperty("contract"), "principal"));
    }

    [Fact]
    public async Task A_contract_is_replaced_whole_and_a_stale_version_is_a_conflict()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Golden(app));
        var version = await ContractVersionAsync(app, id);

        var body = Contract("200000", Phase(1, payments: 60), Phase(2, annualRate: "0.04", rateType: "variable"));
        body["version"] = version;
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Loans}/{id}/contract", body)).StatusCode);

        var contract = (await DetailAsync(app, id)).GetProperty("contract");
        Assert.Equal([1, 2], contract.GetProperty("phases").EnumerateArray().Select(p => p.GetProperty("seq").GetInt32()));
        Assert.Equal(("0.04", "variable"), (S(contract.GetProperty("phases")[1], "annualRate"), S(contract.GetProperty("phases")[1], "rateType")));
        Assert.NotEqual(version, contract.GetProperty("version").GetUInt32());

        body["principal"] = "150000";
        Assert.Equal((HttpStatusCode.Conflict, "realestate.loan_conflict", (string?)null),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Loans}/{id}/contract", body)));
        body.Remove("version");
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "request.required", "version"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Loans}/{id}/contract", body)));
        Assert.Equal("200000.00", S((await DetailAsync(app, id)).GetProperty("contract"), "principal"));
    }

    [Fact]
    public async Task A_contract_that_makes_a_stored_repayment_impossible_is_refused_at_its_rank()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Golden(app));
        foreach (var (onDate, amount) in new[] { ("2024-03-15", "100000"), ("2022-03-15", "5000") })
            await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Loans}/{id}/repayments", new { onDate, amount, effect = "reduce_term" }));

        // 90 000 leaves about 73 000 in March 2024: the second repayment by date, 100 000, no longer fits.
        var body = Contract("90000");
        body["version"] = await ContractVersionAsync(app, id);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.event_above_balance", "repayments[1].amount"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Loans}/{id}/contract", body)));

        var detail = await DetailAsync(app, id);
        Assert.Equal("200000.00", S(detail.GetProperty("contract"), "principal"));
        Assert.Equal("100000.00", S(detail.GetProperty("repayments")[1], "amount"));
    }

    [Fact]
    public async Task Another_adults_private_loan_and_an_income_source_are_not_found_on_any_route()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await NewAsync(app, Golden(app, name: "Prêt de B", visibility: "private", borrowers: [app.AdultB]), app.AdultB);
        var source = (await app.NewIncomeSourceAsync("Salaire", app.AdultA)).Value;

        foreach (var target in new[] { hidden, source })
        foreach (var (method, url, body) in Routes(target))
        {
            var response = await app.RawAsync(app.AdultA, method, url, body);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {url}: {response.StatusCode}");
        }

        var mine = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Loans}?scope=me&ended=true"))).GetProperty("items");
        Assert.DoesNotContain(hidden, mine.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()));
    }

    private static IEnumerable<(HttpMethod Method, string Url, object? Body)> Routes(Guid id)
    {
        var other = Guid.NewGuid();
        var contract = Contract();
        contract["version"] = 1;
        return
        [
            (HttpMethod.Get, $"{Loans}/{id}", null),
            (HttpMethod.Get, $"{Loans}/{id}/schedule", null),
            (HttpMethod.Post, $"{Loans}/{id}/simulations", new { on = "2025-10-15", amount = "1000" }),
            (HttpMethod.Patch, $"{Loans}/{id}", new { note = "x" }),
            (HttpMethod.Put, $"{Loans}/{id}/contract", contract),
            (HttpMethod.Post, $"{Loans}/{id}/fees", new { kind = "file", amount = "100", onDate = "2020-10-15" }),
            (HttpMethod.Put, $"{Loans}/{id}/fees/{other}", new { kind = "file", amount = "100", onDate = "2020-10-15", version = 1 }),
            (HttpMethod.Delete, $"{Loans}/{id}/fees/{other}?version=1", null),
            (HttpMethod.Post, $"{Loans}/{id}/repayments", new { onDate = "2025-03-15", amount = "1000", effect = "reduce_term" }),
            (HttpMethod.Put, $"{Loans}/{id}/repayments/{other}", new { onDate = "2025-03-15", amount = "1000", effect = "reduce_term", version = 1 }),
            (HttpMethod.Delete, $"{Loans}/{id}/repayments/{other}?version=1", null),
            (HttpMethod.Post, $"{Loans}/{id}/bank-schedule/preview", new { text = BankTable }),
            (HttpMethod.Put, $"{Loans}/{id}/bank-schedule", new { text = BankTable, version = 1 }),
            (HttpMethod.Delete, $"{Loans}/{id}/bank-schedule?version=1", null),
        ];
    }
}
