using System.Net;
using Nid.TestSupport;
using static Nid.Subscriptions.IntegrationTests.RealEstate.LoanApi;

namespace Nid.Subscriptions.IntegrationTests.RealEstate;

/// <summary>Loans dashboard spec §3.4: <c>GET /loans/overview</c>, its scope, and another adult's private loan.</summary>
public class LoansOverviewEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Overview = $"{Loans}/overview";

    [Fact]
    public async Task The_overview_adds_up_the_running_loans_at_the_hosts_today()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewAsync(app, Golden(app));
        var works = await NewAsync(app, Short(app)); // 1 000 a month in 2025, 2 000 left after today's payment

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Overview}?scope=household");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(("EUR", 2, 0), (S(body, "currency"), body.GetProperty("runningCount").GetInt32(), body.GetProperty("endedCount").GetInt32()));
        Assert.Equal(("164253.12", "212000.00", "2159.92"),
            (S(body.GetProperty("balance"), "amount"), S(body.GetProperty("principal"), "amount"), S(body.GetProperty("monthlyPayment"), "amount")));
        Assert.Equal(("2040-10-15", "2025-11-15", "2159.92"),
            (S(body, "freeOn"), S(body, "nextPaymentOn"), S(body.GetProperty("nextPaymentTotal"), "amount")));
        Assert.Equal([home, works], body.GetProperty("loans").EnumerateArray().Select(l => l.GetProperty("id").GetGuid()));

        var months = body.GetProperty("months");
        Assert.Equal(("2025-10", "164253.12"), (S(months[0], "month"), S(months[0].GetProperty("total"), "amount")));
        Assert.Equal("2040-10", S(months[months.GetArrayLength() - 1], "month"));

        var ends = body.GetProperty("events")[0];
        Assert.Equal(("2025-12-15", "realestate.loan_ends", "1159.92"),
            (S(ends, "date"), S(ends, "code"), S(ends.GetProperty("monthlyPaymentAfter"), "amount")));
        Assert.Equal(works, ends.GetProperty("loanId").GetGuid());
        Assert.Empty(body.GetProperty("findings").EnumerateArray());

        // Spec §4.1: the timeline, the payment in steps, A's share of both.
        Assert.Equal([home, works], body.GetProperty("spans").EnumerateArray().Select(s => s.GetProperty("loanId").GetGuid()));
        Assert.Equal(("2020-10-15", "2040-10-15"), (S(body.GetProperty("spans")[0], "start"), S(body.GetProperty("spans")[0], "end")));
        var steps = body.GetProperty("steps");
        Assert.Equal(("2025-10", "2025-12", "2159.92"), (S(steps[0], "from"), S(steps[0], "to"), S(steps[0].GetProperty("amount"), "amount")));
        Assert.Equal(("2026-01", "1159.92"), (S(steps[1], "from"), S(steps[1].GetProperty("amount"), "amount")));
        var share = Assert.Single(body.GetProperty("byPerson").EnumerateArray());
        Assert.Equal(app.AdultA.Value, share.GetProperty("personId").GetGuid());
        Assert.Equal(("164253.12", "2159.92"), (S(share.GetProperty("balance"), "amount"), S(share.GetProperty("monthlyPayment"), "amount")));
    }

    [Fact]
    public async Task Another_adults_private_loan_is_in_no_figure_and_the_scope_applies()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await NewAsync(app, Golden(app, borrowers: [app.AdultB]), app.AdultB);
        var hidden = await NewAsync(app, Golden(app, name: "Prêt de B", visibility: "private", borrowers: [app.AdultB]), app.AdultB);

        var household = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Overview}?scope=household"));
        var mine = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Overview}?scope=me"));
        var theirs = await JsonAsync(await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Overview}?scope=me"));

        Assert.Equal([shared], household.GetProperty("loans").EnumerateArray().Select(l => l.GetProperty("id").GetGuid()));
        Assert.Equal("162253.12", S(household.GetProperty("balance"), "amount"));
        Assert.DoesNotContain(hidden.ToString(), household.ToString());
        // Moi: the shared loans and the reader's own private ones.
        Assert.Equal([shared], mine.GetProperty("loans").EnumerateArray().Select(l => l.GetProperty("id").GetGuid()));
        Assert.Equal(2, theirs.GetProperty("runningCount").GetInt32());
        Assert.Contains(hidden, theirs.GetProperty("loans").EnumerateArray().Select(l => l.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Without_a_loan_the_overview_is_empty()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var body = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, Overview));

        Assert.Equal((0, 0), (body.GetProperty("runningCount").GetInt32(), body.GetProperty("endedCount").GetInt32()));
        Assert.Empty(body.GetProperty("loans").EnumerateArray());
        Assert.Empty(body.GetProperty("months").EnumerateArray());
    }
}

/// <summary>Loans dashboard spec §5.4: <c>POST /loans/overview/simulations</c>, its refusals, and another adult's private loan.</summary>
public class LoansSimulationEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Simulations = $"{Loans}/overview/simulations";

    [Fact]
    public async Task The_highest_rate_takes_the_amount_and_nothing_is_recorded()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewAsync(app, Golden(app));
        var works = await NewAsync(app, Short(app)); // 0 %, 2 000 left

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, Simulations,
            new { scope = "household", on = "2025-11-15", amount = "5000", split = "highest_rate", effect = "reduce_term" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        var loans = body.GetProperty("loans");
        Assert.Equal([home, works], loans.EnumerateArray().Select(l => l.GetProperty("loanId").GetGuid()));
        Assert.Equal(("5000.00", "0.00"), (S(loans[0], "amount"), S(loans[1], "amount")));
        Assert.True(loans[0].GetProperty("canWrite").GetBoolean());
        Assert.Equal(("EUR", "2025-12"), (S(body, "currency"), S(body, "paymentMonth")));
        Assert.Equal(S(loans[0], "netSaving"), S(body.GetProperty("netSaving"), "amount"));
        Assert.Empty((await DetailAsync(app, home)).GetProperty("repayments").EnumerateArray());
    }

    [Fact]
    public async Task Each_refusal_answers_at_its_field()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewAsync(app, Golden(app));
        var works = await NewAsync(app, Short(app));
        var cases = new (object Body, string Code, string Path)[]
        {
            (new { on = "2025-10-14", amount = "1000", split = "highest_rate", effect = "reduce_term" }, "realestate.invalid_loan", "on"),
            (new { on = "2025-11-15", amount = "0", split = "highest_rate", effect = "reduce_term" }, "realestate.invalid_loan", "amount"),
            (new { on = "2025-11-15", amount = "1000", split = "chosen", effect = "reduce_term" }, "request.required", "loanId"),
            (new { on = "2025-11-15", amount = "1000", split = "highest_rate", loanId = home, effect = "reduce_term" }, "realestate.invalid_loan", "loanId"),
            (new { on = "2025-11-15", amount = "1000", split = "evenly", effect = "reduce_term" }, "realestate.invalid_loan", "split"),
            (new { on = "2025-11-15", amount = "3000", split = "chosen", loanId = works, effect = "reduce_term" }, "realestate.event_above_balance", "amount"),
            (new { on = "2025-11-15", amount = "1000", split = "chosen", loanId = home, effect = "reduce_term", penalties = new[] { new { loanId = works, amount = "10" } } },
                "realestate.penalty_without_repayment", "penalties[0].loanId"),
        };

        foreach (var (body, code, path) in cases)
        {
            var (status, actualCode, actualPath) = await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Simulations, body));
            Assert.True((HttpStatusCode.UnprocessableEntity, code, path) == (status, actualCode, actualPath), $"{path}: {status} {actualCode} {actualPath}");
        }
    }

    [Fact]
    public async Task Another_adults_private_loan_is_neither_split_nor_chosen()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await NewAsync(app, Golden(app));
        var hidden = await NewAsync(app, Golden(app, name: "Prêt de B", visibility: "private", borrowers: [app.AdultB]), app.AdultB);

        var split = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Simulations,
            new { scope = "me", on = "2025-11-15", amount = "1000", split = "smallest_balance", effect = "reduce_term" }));
        var chosen = await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Simulations,
            new { on = "2025-11-15", amount = "1000", split = "chosen", loanId = hidden, effect = "reduce_term" }));

        Assert.Equal([shared], split.GetProperty("loans").EnumerateArray().Select(l => l.GetProperty("loanId").GetGuid()));
        Assert.DoesNotContain(hidden.ToString(), split.ToString());
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_loan", "loanId"), chosen);
    }
}
