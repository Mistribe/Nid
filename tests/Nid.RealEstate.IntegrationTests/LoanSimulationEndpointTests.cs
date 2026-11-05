using System.Net;
using System.Text.Json;
using Nid.TestSupport;
using static Nid.TestSupport.LoanApi;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>Issues #403 and #404: a reader simulates, nothing is stored; the contract prepared is one the contract route takes.</summary>
public class LoanSimulationEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<JsonElement> PutPreparedAsync(NidApp app, Guid id, JsonElement contract)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Loans}/{id}/contract", JsonSerializer.Deserialize<object>(contract.GetRawText()));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return await DetailAsync(app, id);
    }

    [Fact]
    public async Task A_renegotiation_compares_both_loans_and_prepares_the_contract_without_storing_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Golden(app)); // A's alone, shared: B reads it and cannot write it
        var url = $"{Loans}/{id}/simulations/renegotiation";

        // On the 60th payment (2025-10-15): 180 payments left, 46 532,34 of interest at 3,5 %.
        var sim = await JsonAsync(await app.RawAsync(app.AdultB, HttpMethod.Post, url,
            new { on = "2025-10-15", annualRate = "0.02", bankFees = "1000", guaranteeFees = "500", penalty = "500" }));
        var current = sim.GetProperty("current");
        var next = sim.GetProperty("renegotiated");
        Assert.Equal(("1159.92", "2040-10-15", 180, "46532.34", "0.00"),
            (S(current, "payment"), S(current, "endDate"), current.GetProperty("paymentsLeft").GetInt32(), S(current, "interestLeft"), S(current, "fees")));
        Assert.Equal(("2040-10-15", 180, "2000.00", "2000.00"), (S(next, "endDate"), next.GetProperty("paymentsLeft").GetInt32(), S(next, "fees"), S(sim, "fees")));
        Assert.Equal("2026-09-15", S(sim, "breakEvenOn"));
        var phases = sim.GetProperty("contract").GetProperty("phases").EnumerateArray().ToList();
        Assert.Equal([(1, "0.035", (int?)60), (2, "0.02", null)],
            phases.Select(p => (p.GetProperty("seq").GetInt32(), S(p, "annualRate"), p.GetProperty("payments").ValueKind == JsonValueKind.Null ? (int?)null : p.GetProperty("payments").GetInt32())));
        Assert.Equal(1, (await DetailAsync(app, id, app.AdultB)).GetProperty("contract").GetProperty("phases").GetArrayLength());

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_loan", "annualRate"),
            await RefusalAsync(await app.RawAsync(app.AdultB, HttpMethod.Post, url, new { on = "2025-10-15", annualRate = "1.5" })));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_loan", "penalty"),
            await RefusalAsync(await app.RawAsync(app.AdultB, HttpMethod.Post, url, new { on = "2025-10-15", annualRate = "0.02", penalty = "-1" })));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_loan", "on"),
            await RefusalAsync(await app.RawAsync(app.AdultB, HttpMethod.Post, url, new { on = "2040-10-15", annualRate = "0.02" })));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_loan", "payments"),
            await RefusalAsync(await app.RawAsync(app.AdultB, HttpMethod.Post, url, new { on = "2025-10-15", annualRate = "0.02", payments = 0 })));

        // « Enregistrer »: the writer sends the prepared contract as it is; the loan then reads as the simulation did.
        var saved = await PutPreparedAsync(app, id, sim.GetProperty("contract"));
        Assert.Equal(S(next, "endDate"), S(saved.GetProperty("figures"), "endDate"));
    }

    [Fact]
    public async Task An_insurance_quote_is_compared_per_insured_person_and_prepares_the_change()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Golden(app);
        body["insurances"] = new object[]
        {
            new { mode = "rate_on_initial", annualRate = "0.003", startsOn = "2020-11-15", note = "Alice" }, // 50,00 a month
            // A day later: lines of one start date come back in id order, not the order sent.
            new { mode = "fixed_monthly", monthlyAmount = "20", startsOn = "2020-11-16", note = "Bob" },
        };
        var id = await NewAsync(app, body);
        var url = $"{Loans}/{id}/simulations/insurance";

        // From the 61st payment (2025-11-15): 180 payments left.
        var c = await JsonAsync(await app.RawAsync(app.AdultB, HttpMethod.Post, url,
            new { on = "2025-11-01", quotes = new[] { new { insurance = 0, mode = "rate_on_remaining", annualRate = "0.001" } } }));
        var lines = c.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal([(0, "9000.00", true), (1, "3600.00", false)],
            lines.Select(l => (l.GetProperty("insurance").GetInt32(), S(l, "currentLeft"), l.GetProperty("quote").ValueKind != JsonValueKind.Null)));
        Assert.Equal(("rate_on_remaining", "2025-11-15"), (S(lines[0].GetProperty("quote"), "mode"), S(lines[0].GetProperty("quote"), "startsOn")));
        Assert.Equal("12600.00", S(c, "currentLeft"));
        var insurances = c.GetProperty("contract").GetProperty("insurances").EnumerateArray().ToList();
        Assert.Equal([("rate_on_initial", "2025-11-15", "Alice"), ("rate_on_remaining", null, null), ("fixed_monthly", null, "Bob")],
            insurances.Select(i => (S(i, "mode"), S(i, "endsOn"), S(i, "note"))));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_loan", "quotes[0].insurance"),
            await RefusalAsync(await app.RawAsync(app.AdultB, HttpMethod.Post, url, new { on = "2025-11-01", quotes = new[] { new { insurance = 2, mode = "fixed_monthly", monthlyAmount = "10" } } })));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_loan", "quotes[0].monthlyAmount"),
            await RefusalAsync(await app.RawAsync(app.AdultB, HttpMethod.Post, url, new { on = "2025-11-01", quotes = new[] { new { insurance = 0, mode = "fixed_monthly" } } })));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "request.required", "quotes"),
            await RefusalAsync(await app.RawAsync(app.AdultB, HttpMethod.Post, url, new { on = "2025-11-01" })));
        Assert.Equal(2, (await DetailAsync(app, id, app.AdultB)).GetProperty("contract").GetProperty("insurances").GetArrayLength());

        var saved = await PutPreparedAsync(app, id, c.GetProperty("contract"));
        Assert.Equal(3, saved.GetProperty("contract").GetProperty("insurances").GetArrayLength());
    }

    [Fact]
    public async Task An_insurance_quote_on_a_payment_s_due_date_takes_over_after_it_as_a_renegotiation_does()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Golden(app);
        body["insurances"] = new object[] { new { mode = "rate_on_initial", annualRate = "0.003", startsOn = "2020-11-15" } }; // 50,00 a month
        var id = await NewAsync(app, body);
        var before = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Loans}/{id}/schedule"))).GetProperty("rows").EnumerateArray().ToList();

        // On the 60th payment (2025-10-15), paid as for a renegotiation: the quote pays the 180 after it.
        var c = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Loans}/{id}/simulations/insurance",
            new { on = "2025-10-15", quotes = new[] { new { insurance = 0, mode = "rate_on_remaining", annualRate = "0.001" } } }));
        Assert.Equal("9000.00", S(c, "currentLeft"));

        await PutPreparedAsync(app, id, c.GetProperty("contract"));
        var after = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Loans}/{id}/schedule"))).GetProperty("rows").EnumerateArray().ToList();
        static decimal Insurance(IEnumerable<JsonElement> rows) => rows.Sum(r => decimal.Parse(S(r, "insurance")!, System.Globalization.CultureInfo.InvariantCulture));
        // Nothing counted twice or lost: the payments made keep the old premium, the ones left cost the quote's total.
        Assert.Equal(Insurance(before.Take(60)), Insurance(after.Take(60)));
        Assert.Equal(decimal.Parse(S(c, "quotedLeft")!, System.Globalization.CultureInfo.InvariantCulture), Insurance(after.Skip(60)));
    }
}
