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
