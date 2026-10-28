using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.Subscriptions.IntegrationTests.RealEstate.LoanApi;
using static Nid.Subscriptions.IntegrationTests.RealEstate.PropertyApi;

namespace Nid.Subscriptions.IntegrationTests.Projection;

/// <summary>Net worth projection spec §3 and §7: <c>GET /api/v1/projection</c>, the household's base projection. Today is 2025-10-15.</summary>
public class ProjectionEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Url = "/api/v1/projection";

    private static async Task<JsonElement> ProjectionAsync(NidApp app, PersonId reader, string query = "")
    {
        var response = await app.RawAsync(reader, HttpMethod.Get, Url + query);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task<ItemId> ValuedEnvelopeAsync(NidApp app, PersonId owner, string amount, Visibility visibility = Visibility.Shared, string name = "Livret")
    {
        var envelope = await app.NewEnvelopeAsync(name, owner, visibility);
        Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(owner, HttpMethod.Post, $"/api/v1/investments/envelopes/{envelope.Value}/valuations",
            new { asOf = "2025-09-30", amount = new { amount, currency = "EUR" }, note = (string?)null })).StatusCode);
        return envelope;
    }

    private static async Task ContributionAsync(NidApp app, ItemId envelope, string on, string amount)
        => Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{envelope.Value}/flows",
            new { occurredOn = on, kind = "contribution", amount = new { amount, currency = "EUR" } })).StatusCode);

    private static async Task RetirementAsync(NidApp app, PersonId person, string? birthDate, int age = 64)
        => (await app.RawAsync(app.AdultA, HttpMethod.Put, $"/api/v1/core/persons/{person.Value}/retirement", new { birthDate, retirementAge = age }))
            .EnsureSuccessStatusCode();

    private static async Task<string?> RefusalCodeAsync(NidApp app, string query)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, Url + query);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString();
    }

    private static List<JsonElement> List(JsonElement view, string name) => view.GetProperty(name).EnumerateArray().ToList();

    private static string? Str(JsonElement element, string name) => element.GetProperty(name).GetString();

    [Fact]
    public async Task Default_horizon_is_ten_years()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var view = await ProjectionAsync(app, app.AdultA);

        var points = List(view, "points");
        Assert.Equal(121, points.Count);
        Assert.Equal("2025-10-15", Str(points[0], "on"));
        Assert.Equal("2025-11-30", Str(points[1], "on"));
        Assert.Equal("2035-10-31", Str(points[^1], "on"));
        Assert.Equal(("10y", "2035-10-31", "EUR"), (Str(view, "horizon"), Str(view, "horizonOn"), Str(view, "currency")));
        Assert.Equal("6", view.GetProperty("assumptions").GetProperty("classRates").GetProperty("equity").GetString());
        Assert.Equal(61, List(await ProjectionAsync(app, app.AdultA, "?horizon=5y"), "points").Count);
    }

    [Fact]
    public async Task Point_zero_equals_the_dashboard_net_worth()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await ValuedEnvelopeAsync(app, app.AdultA, "10000.00");
        var home = await NewPropertyAsync(app, Home(app));
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{home}/valuations", new { asOf = "2025-06-01", amount = "300000" }));
        var loan = Golden(app);
        loan["propertyId"] = home;
        await NewAsync(app, loan);

        var point = List(await ProjectionAsync(app, app.AdultA, "?scope=household"), "points")[0];

        var dashboard = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/dashboard?scope=household")).RootElement
            .GetProperty("netWorth").GetProperty("total").GetProperty("amount").GetString();
        Assert.Equal(dashboard, Str(point, "nominal"));
        Assert.Equal(Str(point, "nominal"), Str(point, "real"));
        Assert.Equal(("10000.00", "300000.00", "162253.12"), (Str(point, "savings"), Str(point, "property"), Str(point, "debt")));
        Assert.Equal(("10000.00", "300000.00", "162253.12"), (Str(point, "realSavings"), Str(point, "realProperty"), Str(point, "realDebt")));
    }

    [Fact]
    public async Task Real_parts_are_divided_by_inflation()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await ValuedEnvelopeAsync(app, app.AdultA, "10000.00");

        var view = await ProjectionAsync(app, app.AdultA);

        var year = List(view, "points")[12]; // no property nor debt: the real savings are the real net worth
        Assert.NotEqual(Str(year, "savings"), Str(year, "realSavings"));
        Assert.Equal((Str(year, "real"), "0.00", "0.00"), (Str(year, "realSavings"), Str(year, "realProperty"), Str(year, "realDebt")));
    }

    [Fact]
    public async Task Retirement_horizons_are_the_adults_ahead_within_40_years()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await RetirementAsync(app, app.AdultA, "1990-05-10");
        await RetirementAsync(app, app.AdultB, "1950-01-01"); // already retired
        await RetirementAsync(app, app.Dependent, "1992-06-01"); // in 2056, within 40 years, but a dependent

        var view = await ProjectionAsync(app, app.AdultA);

        var horizon = Assert.Single(List(view, "retirementHorizons"));
        var name = (await app.GetAsync<JsonElement>(app.AdultA, "/api/v1/core/persons")).EnumerateArray()
            .Single(p => p.GetProperty("id").GetGuid() == app.AdultA.Value).GetProperty("displayName").GetString();
        Assert.Equal((app.AdultA.Value, name, "2054-05-31"), (horizon.GetProperty("person").GetGuid(), Str(horizon, "name"), Str(horizon, "on")));
        Assert.Equal(Str(horizon, "on"), Str(await ProjectionAsync(app, app.AdultA, $"?horizon=retire:{app.AdultA.Value}"), "horizonOn"));
    }

    [Fact]
    public async Task Automatic_savings_is_the_12_month_average()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await ValuedEnvelopeAsync(app, app.AdultA, "5000.00");
        await ContributionAsync(app, envelope, "2024-10-15", "900.00"); // a day before the 12 months
        await ContributionAsync(app, envelope, "2024-10-16", "1000.00");
        await ContributionAsync(app, envelope, "2025-10-15", "200.00");

        var view = await ProjectionAsync(app, app.AdultA);

        Assert.Equal(("100.00", "auto"), (Str(view, "monthlySavings"), Str(view, "savingsOrigin")));
        Assert.Equal(JsonValueKind.Null, view.GetProperty("assumptions").GetProperty("monthlySavings").ValueKind);
    }

    [Fact]
    public async Task Household_savings_override_the_average()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await ValuedEnvelopeAsync(app, app.AdultA, "5000.00");
        await ContributionAsync(app, envelope, "2025-05-15", "1200.00");
        var assumptions = await app.GetAsync<JsonElement>(app.AdultA, "/api/v1/core/projection-assumptions");
        (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/core/projection-assumptions", new
        {
            classRates = assumptions.GetProperty("classRates"), inflation = "2", monthlySavings = "500", savingsClass = "cash",
            freedPaymentsToSavings = true,
        })).EnsureSuccessStatusCode();

        var view = await ProjectionAsync(app, app.AdultA);

        Assert.Equal(("500.00", "household"), (Str(view, "monthlySavings"), Str(view, "savingsOrigin")));
        Assert.Equal("500.00", view.GetProperty("assumptions").GetProperty("monthlySavings").GetString());
    }

    [Fact]
    public async Task Loan_end_is_a_milestone()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await NewAsync(app, Short(app)); // last payment 2025-12-15

        var view = await ProjectionAsync(app, app.AdultA);

        var milestone = Assert.Single(List(view, "milestones"));
        Assert.Equal(("2025-12-15", "loan_end", "Prêt travaux"), (Str(milestone, "on"), Str(milestone, "kind"), Str(milestone, "label")));
        Assert.Equal(loan, milestone.GetProperty("item").GetGuid());
        var points = List(view, "points");
        Assert.Equal("1000.00", Str(points[1], "debt")); // 2025-11-30: one payment left
        Assert.Equal("0.00", Str(points[2], "debt"));
    }

    [Fact]
    public async Task Retirement_horizon_ends_at_the_birthday_month()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await RetirementAsync(app, app.AdultA, "1990-05-10");

        var view = await ProjectionAsync(app, app.AdultA, $"?horizon=retire:{app.AdultA.Value}");

        Assert.Equal(("2054-05-31", $"retire:{app.AdultA.Value}"), (Str(view, "horizonOn"), Str(view, "horizon")));
        Assert.Equal("2054-05-31", Str(List(view, "points")[^1], "on"));
        var name = (await app.GetAsync<JsonElement>(app.AdultA, "/api/v1/core/persons")).EnumerateArray()
            .Single(p => p.GetProperty("id").GetGuid() == app.AdultA.Value).GetProperty("displayName").GetString();
        var retirement = Assert.Single(List(view, "milestones"));
        Assert.Equal(("2054-05-31", "retirement", name), (Str(retirement, "on"), Str(retirement, "kind"), Str(retirement, "label")));
        Assert.Equal(JsonValueKind.Null, retirement.GetProperty("item").ValueKind);
    }

    [Fact]
    public async Task Retirement_without_birth_date_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);

        Assert.Equal("projection.no_birth_date", await RefusalCodeAsync(app, $"?horizon=retire:{app.AdultA.Value}"));
        Assert.Equal("projection.no_birth_date", await RefusalCodeAsync(app, $"?horizon=retire:{Guid.NewGuid()}"));
    }

    [Fact]
    public async Task Retirement_passed_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await RetirementAsync(app, app.AdultA, "1950-01-01");
        await RetirementAsync(app, app.AdultB, "1961-10-01"); // retires this month: no month to project
        await RetirementAsync(app, app.Dependent, "2015-06-01", 75); // in 2090, past 40 years

        Assert.Equal("projection.invalid_horizon", await RefusalCodeAsync(app, $"?horizon=retire:{app.AdultA.Value}"));
        Assert.Equal("projection.invalid_horizon", await RefusalCodeAsync(app, $"?horizon=retire:{app.AdultB.Value}"));
        Assert.Equal("projection.invalid_horizon", await RefusalCodeAsync(app, $"?horizon=retire:{app.Dependent.Value}"));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0y")]
    [InlineData("41y")]
    [InlineData("10")]
    [InlineData("retire:nobody")]
    public async Task Unknown_horizon_is_400(string horizon)
    {
        await using var app = await NidApp.StartAsync(fixture);

        Assert.Equal(HttpStatusCode.BadRequest, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url}?horizon={horizon}")).StatusCode);
    }

    [Fact]
    public async Task Empty_household_gives_zero_points()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var view = await ProjectionAsync(app, app.AdultA);

        Assert.All(List(view, "points"), p => Assert.Equal(("0.00", "0.00", "0.00", "0.00", "0.00"),
            (Str(p, "nominal"), Str(p, "real"), Str(p, "savings"), Str(p, "property"), Str(p, "debt"))));
        Assert.Equal(("0.00", "auto"), (Str(view, "monthlySavings"), Str(view, "savingsOrigin")));
        Assert.Empty(List(view, "milestones"));
        Assert.Empty(List(view, "warnings"));
    }

    [Fact]
    public async Task Foreign_loan_without_rate_is_left_out_and_named()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var pounds = Short(app);
        pounds["currency"] = "GBP"; // no GBP rate is stored
        await NewAsync(app, pounds);

        var view = await ProjectionAsync(app, app.AdultA);

        Assert.All(List(view, "points"), p => Assert.Equal("0.00", Str(p, "debt")));
        var warning = Assert.Single(List(view, "warnings"));
        Assert.Equal(("fx_missing", "GBP"), (Str(warning, "code"), Str(warning, "detail")));
    }

    [Fact]
    public async Task Extended_income_is_warned()
    {
        await using var app = await NidApp.StartAsync(fixture);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/income/sources", new Dictionary<string, object?>
        {
            ["displayName"] = "Mon CDD", ["kind"] = "salary", ["currency"] = "EUR", ["visibility"] = "shared",
            ["owners"] = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            ["phases"] = new[]
            {
                new
                {
                    label = "CDD", startsOn = "2025-01-01", endsOn = "2026-03-01", netAmount = new { amount = "2000", currency = "EUR" },
                    every = new { count = 1, unit = "month" },
                },
            },
        })).EnsureSuccessStatusCode();

        var view = await ProjectionAsync(app, app.AdultA);

        var warning = Assert.Single(List(view, "warnings"));
        Assert.Equal(("income_end_extended", "Mon CDD"), (Str(warning, "code"), Str(warning, "detail")));
    }

    [Fact]
    public async Task Private_items_of_the_other_adult_change_nothing() // spec §6
    {
        await using var app = await NidApp.StartAsync(fixture);
        await ValuedEnvelopeAsync(app, app.AdultA, "8000.00");
        await ValuedEnvelopeAsync(app, app.AdultB, "50000.00", Visibility.Private, "Secret");
        await ContributionAsync(app, await ValuedEnvelopeAsync(app, app.AdultA, "100.00", name: "PEA"), "2025-06-01", "600.00");
        var hidden = Short(app, "Prêt secret");
        hidden["visibility"] = "private";
        hidden["borrowers"] = new[] { new { personId = app.AdultB.Value, shareBp = 10_000 } };
        var secretLoan = await NewAsync(app, hidden, app.AdultB);

        var household = await ProjectionAsync(app, app.AdultA, "?scope=household");
        var me = await ProjectionAsync(app, app.AdultA, "?scope=me");

        Assert.Equal(List(household, "points").Select(p => p.GetRawText()), List(me, "points").Select(p => p.GetRawText()));
        Assert.Equal("8100.00", Str(List(household, "points")[0], "savings"));
        Assert.Equal("50.00", Str(household, "monthlySavings"));
        Assert.DoesNotContain(List(household, "milestones"), m => m.GetProperty("item").ValueKind != JsonValueKind.Null
            && m.GetProperty("item").GetGuid() == secretLoan);
        Assert.DoesNotContain(List(household, "milestones"), m => Str(m, "label") == "Prêt secret");
        // Positive control: a private item is in its owner's « Moi » only, where B counts them.
        var forB = await ProjectionAsync(app, app.AdultB, "?scope=me");
        Assert.Contains(List(forB, "milestones"), m => Str(m, "label") == "Prêt secret");
        Assert.Equal("58100.00", Str(List(forB, "points")[0], "savings"));
    }
}
