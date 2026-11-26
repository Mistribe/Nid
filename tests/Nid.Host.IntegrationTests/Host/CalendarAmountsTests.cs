using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>Issue #241: the calendar's amounts, the money expected in and out over a month. Today is 2025-10-15 (<see cref="NidApp.GoldenCaseToday"/>).</summary>
public class CalendarAmountsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string October = "/api/v1/calendar?from=2025-10-01&to=2025-10-31";

    private static async Task<JsonElement> GetAsync(NidApp app, PersonId reader, string url)
        => JsonDocument.Parse(await app.GetRawAsync(reader, url)).RootElement;

    private static string? Total(JsonElement figure)
        => figure.GetProperty("total").ValueKind == JsonValueKind.Null ? null : figure.GetProperty("total").GetProperty("amount").GetString();

    private static IEnumerable<string> Charges(JsonElement view)
        => view.GetProperty("amounts").GetProperty("charges").EnumerateArray()
            .Select(c => $"{c.GetProperty("on").GetString()} {c.GetProperty("kind").GetString()} {c.GetProperty("module").GetString()} {c.GetProperty("name").GetString()} "
                         + $"{c.GetProperty("amount").GetProperty("amount").GetString()}{(c.GetProperty("credit").GetBoolean() ? " in" : "")}");

    /// <summary>
    /// A month of the household's: a subscription on the 1st and one on the 5th, the golden loan's instalment on the 15th, a property's
    /// yearly tax on the 20th and its rent (a credit) on the 1st.
    /// </summary>
    private static async Task HouseholdAsync(NidApp app)
    {
        await app.NewMonthlyAsync(app.AdultA, 20m);
        var club = await app.NewSubscriptionAsync("Club", app.AdultA, Visibility.Shared);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{club.Value}/phases", new
        {
            startsOn = "2025-01-05", price = new { amount = "5.00", currency = "EUR" }, every = new { count = 1, unit = "month" }, payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        await NewAsync(app, Golden(app)); // 1 159.92 on the 15th
        var home = await NewPropertyAsync(app, Home(app));
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{home}/recurring-costs",
            new { category = "property_tax", amount = "1200", every = new { count = 1, unit = "year" }, startsOn = "2020-10-20" })).EnsureSuccessStatusCode();
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{home}/tenancies",
            new { label = "T2", startsOn = "2021-01-01", rentMonthly = "900" })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Each_charge_shows_on_its_day_and_the_month_out_matches_the_dashboards_cash_commitments()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await HouseholdAsync(app);

        var view = await GetAsync(app, app.AdultA, October + "&scope=household&amounts=true");

        Assert.Equal([
            "2025-10-01 subscription subscriptions monthly-20 20.00",
            "2025-10-01 rent properties Maison 900.00 in",
            "2025-10-05 subscription subscriptions Club 5.00",
            "2025-10-15 loan_instalment loans Prêt maison 1159.92",
            "2025-10-20 property_cost properties Maison 1200.00",
        ], Charges(view).OrderBy(c => c[..10]).ThenBy(c => c.Contains(" in") ? 1 : 0));
        var amounts = view.GetProperty("amounts");
        Assert.Equal("EUR", amounts.GetProperty("currency").GetString());
        Assert.Equal(("2384.92", "900.00", "-1484.92"),
            (Total(amounts.GetProperty("out")), Total(amounts.GetProperty("in")), Total(amounts.GetProperty("balance"))));

        // Per day: out and in apart, the three largest days out stand out (the 5th's 5 € does not).
        var days = amounts.GetProperty("days").EnumerateArray().ToDictionary(d => d.GetProperty("on").GetString()!);
        Assert.Equal(["2025-10-01", "2025-10-05", "2025-10-15", "2025-10-20"], days.Keys);
        Assert.Equal(("20.00", "900.00"), (Total(days["2025-10-01"].GetProperty("out")), Total(days["2025-10-01"].GetProperty("in"))));
        Assert.Equal(JsonValueKind.Null, days["2025-10-20"].GetProperty("in").ValueKind);
        Assert.Equal(["2025-10-01", "2025-10-15", "2025-10-20"], days.Values.Where(d => d.GetProperty("heavy").GetBoolean()).Select(d => d.GetProperty("on").GetString()));

        // « Engagements »'s cash for the month: the same subscriptions, instalment and property cost. A one-off purchase (in the
        // dashboard's cash, never an expected debit) and childcare (the dashboard counts this month's cost, the calendar the Pajemploi
        // debit of the month validated before) would part them; a rent is money in, never a commitment.
        var dashboard = await GetAsync(app, app.AdultA, "/api/v1/dashboard?scope=household");
        Assert.Equal(Total(dashboard.GetProperty("cashThisMonth")), Total(amounts.GetProperty("out")));
    }

    /// <summary>#714: each income source's payment is money in on its day, under the income module.</summary>
    [Fact]
    public async Task An_income_sources_payments_are_money_in()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await ProjectionApi.IncomeSourceAsync(app, "Salaire Alice", "salary", "2025-01-28", "2500");

        var view = await GetAsync(app, app.AdultA, October + "&amounts=true");
        Assert.Equal(["2025-10-28 income income Salaire Alice 2500.00 in"], Charges(view));
        Assert.Equal(("0.00", "2500.00"), (Total(view.GetProperty("amounts").GetProperty("out")), Total(view.GetProperty("amounts").GetProperty("in"))));
        Assert.Empty(Charges(await GetAsync(app, app.AdultA, October + "&amounts=true&modules=loans")));
    }

    [Fact]
    public async Task Without_amounts_the_view_has_none_and_a_month_with_no_charge_totals_zero()
    {
        await using var app = await NidApp.StartAsync(fixture);
        Assert.Equal(JsonValueKind.Null, (await GetAsync(app, app.AdultA, October)).GetProperty("amounts").ValueKind);

        var amounts = (await GetAsync(app, app.AdultA, October + "&amounts=true")).GetProperty("amounts");
        Assert.Equal(("0.00", "0.00", "0.00"), (Total(amounts.GetProperty("out")), Total(amounts.GetProperty("in")), Total(amounts.GetProperty("balance"))));
        Assert.Empty(amounts.GetProperty("days").EnumerateArray());
    }

    [Fact]
    public async Task Only_the_modules_asked_are_counted_and_childcare_is_a_module_with_amounts_only()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await HouseholdAsync(app);

        var loans = await GetAsync(app, app.AdultA, October + "&amounts=true&modules=loans");
        Assert.Equal(["2025-10-15 loan_instalment loans Prêt maison 1159.92"], Charges(loans));
        Assert.Equal("1159.92", Total(loans.GetProperty("amounts").GetProperty("out")));

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Get, October + "&amounts=true&modules=childcare")).StatusCode);
        var refused = await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, October + "&modules=childcare"));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, TokenCodes.CalendarModulesInvalid, "modules"), refused);
    }

    [Fact]
    public async Task Another_adults_private_loan_is_never_counted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewAsync(app, Golden(app, "Prêt de Bob", "private", app.AdultB), app.AdultB);

        foreach (var scope in new[] { "household", "me" })
        {
            var alice = await GetAsync(app, app.AdultA, October + $"&scope={scope}&amounts=true");
            Assert.Empty(Charges(alice));
            Assert.Equal("0.00", Total(alice.GetProperty("amounts").GetProperty("out")));
        }
        // Bob's own: in his Moi, never in the household's.
        Assert.Single(Charges(await GetAsync(app, app.AdultB, October + "&scope=me&amounts=true")));
        Assert.Empty(Charges(await GetAsync(app, app.AdultB, October + "&scope=household&amounts=true")));
    }
}
