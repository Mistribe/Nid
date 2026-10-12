using System.Net;
using Nid.TestSupport;
using static Nid.Subscriptions.IntegrationTests.RealEstate.LoanApi;

namespace Nid.Subscriptions.IntegrationTests.RealEstate;

/// <summary>Spec 3A2 §5: fees, early repayments and their penalty, the simulation, conflicts and the row lock.</summary>
[Collection("postgres")]
public class LoanFeeAndRepaymentTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_fee_is_added_corrected_and_deleted_against_its_version()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Golden(app));
        var fees = $"{Loans}/{id}/fees";

        var fee = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, fees, new { kind = "file", amount = "1000", onDate = "2020-10-15", note = "dossier" }));
        var detail = await DetailAsync(app, id);
        var view = Only(detail, "fees");
        Assert.Equal(("file", "1000.00", "2020-10-15", "dossier"), (S(view, "kind"), S(view, "amount"), S(view, "onDate"), S(view, "note")));
        Assert.Equal("1000.00", S(detail.GetProperty("figures"), "feesPaid"));
        var version = view.GetProperty("version").GetUInt32();

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{fees}/{fee}",
            new { kind = "guarantee", amount = "1200", onDate = "2020-10-15", version })).StatusCode);
        Assert.Equal((HttpStatusCode.Conflict, "realestate.fee_conflict", (string?)null), await RefusalAsync(await app.RawAsync(app.AdultA,
            HttpMethod.Put, $"{fees}/{fee}", new { kind = "file", amount = "9", onDate = "2020-10-15", version })));
        Assert.Equal(HttpStatusCode.Conflict, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{fees}/{fee}?version={version}")).StatusCode);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "request.required", "version"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{fees}/{fee}")));

        var current = Only(await DetailAsync(app, id), "fees");
        Assert.Equal(("guarantee", "1200.00"), (S(current, "kind"), S(current, "amount")));
        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete,
            $"{fees}/{fee}?version={current.GetProperty("version").GetUInt32()}")).StatusCode);
        Assert.Empty((await DetailAsync(app, id)).GetProperty("fees").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{fees}/{fee}?version=1")).StatusCode);

        foreach (var (body, path) in new (object Body, string Path)[]
                 {
                     (new { kind = "bribe", amount = "10", onDate = "2020-10-15" }, "kind"),
                     (new { kind = "file", amount = "0", onDate = "2020-10-15" }, "amount"),
                     (new { kind = "file", amount = "10.125", onDate = "2020-10-15" }, "amount"),
                 })
            Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_loan", path),
                await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, fees, body)));
    }

    [Fact]
    public async Task A_penalty_lives_and_dies_with_its_repayment()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Golden(app));
        var repayments = $"{Loans}/{id}/repayments";

        var rid = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, repayments,
            new { onDate = "2025-03-20", amount = "10000", effect = "reduce_payment", penalty = "300", note = "prime" }));
        var detail = await DetailAsync(app, id);
        var repayment = Only(detail, "repayments");
        Assert.Equal(("10000.00", "reduce_payment", "300.00", "prime"), (S(repayment, "amount"), S(repayment, "effect"), S(repayment, "penalty"), S(repayment, "note")));
        var fee = Only(detail, "fees");
        Assert.Equal(("early_repayment_penalty", "300.00", "2025-03-20", rid),
            (S(fee, "kind"), S(fee, "amount"), S(fee, "onDate"), fee.GetProperty("repaymentId").GetGuid()));
        Assert.Contains(detail.GetProperty("warnings").EnumerateArray(), w => S(w, "code") == "event_off_payment_date");

        // The fee routes refuse it: it is the repayment's.
        var feeId = fee.GetProperty("id").GetGuid();
        var feeVersion = fee.GetProperty("version").GetUInt32();
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.fee_linked", "feeId"), await RefusalAsync(await app.RawAsync(app.AdultA,
            HttpMethod.Put, $"{Loans}/{id}/fees/{feeId}", new { kind = "early_repayment_penalty", amount = "1", onDate = "2025-03-20", version = feeVersion })));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.fee_linked", "feeId"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Loans}/{id}/fees/{feeId}?version={feeVersion}")));

        // Changed with it: the same fee, its new amount on the repayment's new date.
        var version = repayment.GetProperty("version").GetUInt32();
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{repayments}/{rid}",
            new { onDate = "2025-04-15", amount = "12000", effect = "reduce_term", penalty = "350", version })).StatusCode);
        detail = await DetailAsync(app, id);
        fee = Only(detail, "fees");
        Assert.Equal((feeId, "350.00", "2025-04-15"), (fee.GetProperty("id").GetGuid(), S(fee, "amount"), S(fee, "onDate")));
        Assert.Equal((HttpStatusCode.Conflict, "realestate.repayment_conflict", (string?)null), await RefusalAsync(await app.RawAsync(app.AdultA,
            HttpMethod.Put, $"{repayments}/{rid}", new { onDate = "2025-04-15", amount = "12000", effect = "reduce_term", version })));

        // Omitted: removed with it; sent again: a new fee.
        version = Only(detail, "repayments").GetProperty("version").GetUInt32();
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{repayments}/{rid}",
            new { onDate = "2025-04-15", amount = "12000", effect = "reduce_term", version })).StatusCode);
        detail = await DetailAsync(app, id);
        Assert.Empty(detail.GetProperty("fees").EnumerateArray());
        Assert.Null(S(Only(detail, "repayments"), "penalty"));

        version = Only(detail, "repayments").GetProperty("version").GetUInt32();
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{repayments}/{rid}",
            new { onDate = "2025-04-15", amount = "12000", effect = "reduce_term", penalty = "200", version })).StatusCode);

        // Deleted: nothing of it is left behind.
        version = Only(await DetailAsync(app, id), "repayments").GetProperty("version").GetUInt32();
        Assert.Equal(HttpStatusCode.Conflict, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{repayments}/{rid}?version={version + 1}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{repayments}/{rid}?version={version}")).StatusCode);
        detail = await DetailAsync(app, id);
        Assert.Empty(detail.GetProperty("repayments").EnumerateArray());
        Assert.Empty(detail.GetProperty("fees").EnumerateArray());
    }

    [Fact]
    public async Task Each_repayment_refusal_answers_at_the_routes_own_field()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Golden(app));
        var repayments = $"{Loans}/{id}/repayments";

        foreach (var (body, code, path) in new (object Body, string Code, string Path)[]
                 {
                     (new { onDate = "2025-11-15", amount = "500000", effect = "reduce_term" }, "realestate.event_above_balance", "amount"),
                     (new { onDate = "2041-01-15", amount = "1000", effect = "reduce_term" }, "realestate.event_above_balance", "amount"),
                     (new { onDate = "2020-10-14", amount = "1000", effect = "reduce_term" }, "realestate.invalid_loan", "onDate"),
                     (new { onDate = "2025-11-15", amount = "1000", effect = "skip" }, "realestate.invalid_loan", "effect"),
                     (new { onDate = "2025-11-15", amount = "1000.001", effect = "reduce_term" }, "realestate.invalid_loan", "amount"),
                     (new { onDate = "2025-11-15", amount = "1000", effect = "reduce_term", penalty = "0" }, "realestate.invalid_loan", "penalty"),
                     (new { onDate = "2025-11-15", amount = "1000", effect = "reduce_term", penalty = "1.234" }, "realestate.invalid_loan", "penalty"),
                 })
            Assert.Equal((HttpStatusCode.UnprocessableEntity, code, path), await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, repayments, body)));

        Assert.Empty((await DetailAsync(app, id)).GetProperty("repayments").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{repayments}/{Guid.NewGuid()}",
            new { onDate = "2025-11-15", amount = "1000", effect = "reduce_term", version = 1 })).StatusCode);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "request.required", "version"), await RefusalAsync(await app.RawAsync(app.AdultA,
            HttpMethod.Put, $"{repayments}/{Guid.NewGuid()}", new { onDate = "2025-11-15", amount = "1000", effect = "reduce_term" })));
    }

    [Fact]
    public async Task Removing_a_repayment_an_imposed_payment_relies_on_is_refused() // Review Focus #4
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Golden(app));
        var rid = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Loans}/{id}/repayments",
            new { onDate = "2020-10-20", amount = "100000", effect = "reduce_term" }));

        // 400 a month covers the interest on the 100 000 left (291.67), never the interest on 200 000 (583.33).
        var contract = Contract("200000", Phase(paymentOverride: "400"));
        contract["version"] = await ContractVersionAsync(app, id);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Loans}/{id}/contract", contract)).StatusCode);

        var version = Only(await DetailAsync(app, id), "repayments").GetProperty("version").GetUInt32();
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.payment_below_interest", "phases[0].paymentOverride"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Loans}/{id}/repayments/{rid}?version={version}")));
        Assert.Equal(rid, Only(await DetailAsync(app, id), "repayments").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Two_repayments_sent_together_never_both_overdraw_the_balance() // Review Focus #2
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Short(app)); // also fetches A's CSRF token: the two requests below only read it

        // 3 000 remain after the September payment: each 2 000 fits alone, not both.
        var sent = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => app.RawAsync(app.AdultA, HttpMethod.Post, $"{Loans}/{id}/repayments",
            new { onDate = "2025-09-20", amount = "2000", effect = "reduce_term" })));

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.UnprocessableEntity], sent.Select(r => r.StatusCode).Order());
        Assert.Single((await DetailAsync(app, id)).GetProperty("repayments").EnumerateArray());
    }

    [Fact]
    public async Task A_reader_who_cannot_write_simulates_and_nothing_is_stored()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Golden(app)); // A's alone, shared: B reads it and cannot write it
        var simulations = $"{Loans}/{id}/simulations";

        var both = await JsonAsync(await app.RawAsync(app.AdultB, HttpMethod.Post, simulations, new { on = "2025-10-15", amount = "10000", penalty = "300" }));
        var outcomes = both.GetProperty("outcomes").EnumerateArray().ToList();
        Assert.Equal(["reduce_term", "reduce_payment"], outcomes.Select(o => S(o, "effect")));
        Assert.Equal((226, "6566.58", "6266.58"), (outcomes[0].GetProperty("paymentsAfter").GetInt32(), S(outcomes[0], "interestSaved"), S(outcomes[0], "netSaving")));
        Assert.Equal(("1159.92", "1088.43", "2867.80"), (S(outcomes[1], "paymentBefore"), S(outcomes[1], "paymentAfter"), S(outcomes[1], "interestSaved")));

        var one = await JsonAsync(await app.RawAsync(app.AdultB, HttpMethod.Post, simulations, new { on = "2025-10-15", amount = "10000", effect = "reduce_term" }));
        Assert.Single(one.GetProperty("outcomes").EnumerateArray());

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.event_above_balance", "amount"),
            await RefusalAsync(await app.RawAsync(app.AdultB, HttpMethod.Post, simulations, new { on = "2025-10-15", amount = "500000" })));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_loan", "amount"),
            await RefusalAsync(await app.RawAsync(app.AdultB, HttpMethod.Post, simulations, new { on = "2025-10-15", amount = "1.001" })));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_loan", "on"),
            await RefusalAsync(await app.RawAsync(app.AdultB, HttpMethod.Post, simulations, new { on = "2020-10-14", amount = "10" })));

        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Post, $"{Loans}/{id}/repayments",
            new { onDate = "2025-10-15", amount = "10000", effect = "reduce_term" })).StatusCode);
        Assert.Empty((await DetailAsync(app, id, app.AdultB)).GetProperty("repayments").EnumerateArray());
        Assert.False((await DetailAsync(app, id, app.AdultB)).GetProperty("canWrite").GetBoolean());
    }
}
