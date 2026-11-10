using System.Net;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Application.Projection;
using Nid.Core.Contracts;
using Nid.Core.Domain.Projection;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.ProjectionApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.Core.IntegrationTests.Projection;

/// <summary>Net worth projection spec §3 and §7: <c>GET /api/v1/projection</c>, the household's base projection. Today is 2025-10-15.</summary>
public class ProjectionEndpointTests(PostgresFixture fixture, SharedNidApp shared) : IClassFixture<PostgresFixture>, IClassFixture<SharedNidApp>
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

    private static async Task RetirementAsync(NidApp app, PersonId person, string? birthDate, int age = 64, int months = 0)
        => (await app.RawAsync(app.AdultA, HttpMethod.Put, $"/api/v1/core/persons/{person.Value}/retirement", new { birthDate, retirementAge = age, retirementMonths = months }))
            .EnsureSuccessStatusCode();

    private static async Task<string?> RefusalCodeAsync(NidApp app, string query)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, Url + query);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString();
    }

    private static List<JsonElement> List(JsonElement view, string name) => view.GetProperty(name).EnumerateArray().ToList();

    private static string? Str(JsonElement element, string name) => element.GetProperty(name).GetString();

    private static decimal Dec(JsonElement element, string name) => decimal.Parse(Str(element, name)!, CultureInfo.InvariantCulture);

    private static readonly LocalDate Today = new(2025, 10, 15);

    /// <summary>Point k's real figure is its nominal one divided by <paramref name="i"/>[k], within the cent each side rounds to.</summary>
    private static void DeflatedBy(IReadOnlyList<decimal> i, List<JsonElement> points, params int[] months)
    {
        foreach (var k in months)
            Assert.InRange(Dec(points[k], "real"), Dec(points[k], "nominal") / i[k] - 0.01m, Dec(points[k], "nominal") / i[k] + 0.01m);
    }

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

    /// <summary>The household's assumptions with every class at <paramref name="volatility"/> percent.</summary>
    private static async Task VolatilityAsync(NidApp app, string volatility)
    {
        var assumptions = await app.GetAsync<JsonElement>(app.AdultA, "/api/v1/core/projection-assumptions");
        (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/core/projection-assumptions", new
        {
            classRates = assumptions.GetProperty("classRates"), inflation = "2", monthlySavings = "100", savingsClass = "cash", freedPaymentsToSavings = true,
            classVolatility = AllocationClasses.All.ToDictionary(c => c, _ => volatility),
        })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Ranges_come_only_when_asked_and_equal_the_points_without_volatility() // ranges spec §3, §4
    {
        await using var app = await NidApp.StartAsync(fixture);
        await ValuedEnvelopeAsync(app, app.AdultA, "10000.00");
        await VolatilityAsync(app, "0");

        Assert.Equal(JsonValueKind.Null, (await ProjectionAsync(app, app.AdultA)).GetProperty("ranges").ValueKind);
        var view = await ProjectionAsync(app, app.AdultA, "?horizon=5y&ranges=true");

        var ranges = view.GetProperty("ranges");
        Assert.Equal((1000, "100"), (ranges.GetProperty("runs").GetInt32(), Str(ranges, "success")));
        var points = List(view, "points");
        var bands = List(ranges, "points");
        Assert.Equal(points.Count, bands.Count);
        Assert.All(points.Zip(bands), pair =>
        {
            var (point, band) = pair;
            Assert.Equal(Str(point, "on"), Str(band, "on"));
            Assert.Equal((Str(point, "nominal"), Str(point, "nominal"), Str(point, "nominal")), (Str(band, "low"), Str(band, "median"), Str(band, "high")));
            Assert.Equal((Str(point, "real"), Str(point, "real"), Str(point, "real")), (Str(band, "lowReal"), Str(band, "medianReal"), Str(band, "highReal")));
        });
    }

    [Fact]
    public async Task Volatility_spreads_the_ranges_the_same_way_each_time()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await ValuedEnvelopeAsync(app, app.AdultA, "10000.00");
        await VolatilityAsync(app, "20");

        var first = await ProjectionAsync(app, app.AdultA, "?horizon=5y&ranges=true");
        var again = await ProjectionAsync(app, app.AdultA, "?horizon=5y&ranges=true");

        var last = List(first.GetProperty("ranges"), "points")[^1];
        Assert.True(Dec(last, "low") < Dec(last, "median") && Dec(last, "median") < Dec(last, "high"), last.GetRawText());
        Assert.Equal(first.GetProperty("ranges").GetRawText(), again.GetProperty("ranges").GetRawText());
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
        await HouseholdSavingsAsync(app, "500");

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
    public async Task Retirement_horizon_includes_the_months() // retirement spec §1
    {
        await using var app = await NidApp.StartAsync(fixture);
        await RetirementAsync(app, app.AdultA, "1990-05-10", 64, 3);

        Assert.Equal("2054-08-31", Str(await ProjectionAsync(app, app.AdultA, $"?horizon=retire:{app.AdultA.Value}"), "horizonOn"));
    }

    [Fact]
    public async Task Every_adult_departure_in_the_grid_is_a_milestone() // retirement spec §1
    {
        await using var app = await NidApp.StartAsync(fixture);
        await RetirementAsync(app, app.AdultA, "1964-01-10");
        await RetirementAsync(app, app.AdultB, "1966-06-10");

        var view = await ProjectionAsync(app, app.AdultA, "?horizon=10y");

        var names = (await app.GetAsync<JsonElement>(app.AdultA, "/api/v1/core/persons")).EnumerateArray()
            .ToDictionary(p => p.GetProperty("id").GetGuid(), p => p.GetProperty("displayName").GetString());
        Assert.Equal(
            [("2028-01-31", names[app.AdultA.Value]), ("2030-06-30", names[app.AdultB.Value])],
            List(view, "milestones").Where(m => Str(m, "kind") == "retirement").Select(m => (Str(m, "on")!, Str(m, "label"))).ToList());
    }

    [Fact]
    public async Task Salary_stops_at_the_departure() // retirement spec §1
    {
        await using var app = await NidApp.StartAsync(fixture);
        await RetirementAsync(app, app.AdultA, "1963-10-10"); // leaves in 2027-10, month 24: the switch is month 25
        var rates = AllocationClasses.All.ToDictionary(c => c, _ => "0");
        (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/core/projection-assumptions", new
        {
            classRates = rates, inflation = "0", monthlySavings = "100", savingsClass = "cash", freedPaymentsToSavings = true,
        })).EnsureSuccessStatusCode();
        var money = new { amount = "2300", currency = "EUR" };
        (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/income/sources", new Dictionary<string, object?>
        {
            ["displayName"] = "Salaire", ["kind"] = "salary", ["currency"] = "EUR", ["visibility"] = "shared",
            ["owners"] = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            ["phases"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["label"] = "Salaire", ["startsOn"] = "2025-01-01", ["endsOn"] = null, ["netAmount"] = money, ["grossAmount"] = null,
                    ["every"] = new { count = 1, unit = "month" }, ["taxableAmount"] = null,
                },
            },
        })).EnsureSuccessStatusCode();

        var savings = List(await ProjectionAsync(app, app.AdultA, "?horizon=3y"), "points")
            .Select(p => decimal.Parse(Str(p, "savings")!, CultureInfo.InvariantCulture)).ToList();

        Assert.Equal(100m, savings[24] - savings[23]); // the salary is still counted
        Assert.Equal(-2200m, savings[25] - savings[24]); // the salary stops: 100 − 2 300
    }

    /// <summary>1 000 in cash, 50 000 in a PER, saving −500 a month at 0 %: the cash runs out in month 3 unless the PER has opened.</summary>
    private static async Task<List<JsonElement>> PerWarningsAsync(NidApp app, string? birthDate)
    {
        await RetirementAsync(app, app.AdultA, birthDate); // 1961-11-10 + 64 y: leaves in 2025-11, the PER opens in month 2
        await ValuedEnvelopeAsync(app, app.AdultA, "1000.00");
        var created = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/envelopes", new
        {
            displayName = "PER", kind = "per", currency = "EUR", visibility = "shared",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
        });
        created.EnsureSuccessStatusCode();
        var per = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{per}/valuations",
            new { asOf = "2025-09-30", amount = new { amount = "50000.00", currency = "EUR" }, note = (string?)null })).EnsureSuccessStatusCode();
        (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/core/projection-assumptions", new
        {
            classRates = AllocationClasses.All.ToDictionary(c => c, _ => "0"), inflation = "0", monthlySavings = "-500", savingsClass = "cash",
            freedPaymentsToSavings = true,
        })).EnsureSuccessStatusCode();
        return List(await ProjectionAsync(app, app.AdultA, "?horizon=5y"), "warnings");
    }

    [Fact]
    public async Task A_per_opens_at_its_holders_departure() // retirement spec §2
    {
        await using var app = await NidApp.StartAsync(fixture);
        Assert.DoesNotContain(await PerWarningsAsync(app, "1961-11-10"), w => Str(w, "code") == "savings_exhausted");
    }

    [Fact]
    public async Task A_per_is_open_from_the_start_after_a_past_departure() // retirement spec §2
    {
        await using var app = await NidApp.StartAsync(fixture);
        Assert.DoesNotContain(await PerWarningsAsync(app, "1950-01-01"), w => Str(w, "code") == "savings_exhausted");
    }

    [Fact]
    public async Task A_per_stays_locked_without_a_departure() // retirement spec §2
    {
        await using var app = await NidApp.StartAsync(fixture);
        Assert.Contains(await PerWarningsAsync(app, null), w => Str(w, "code") == "savings_exhausted");
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
        var app = shared.App;

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
        Assert.Equal(JsonValueKind.Null, view.GetProperty("financialIndependence").ValueKind); // no living cost: no date (retirement spec §3)
        var warning = Assert.Single(List(view, "warnings"));
        Assert.Equal(("living_cost_missing", JsonValueKind.Null), (Str(warning, "code"), warning.GetProperty("detail").ValueKind));
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
        var warning = Assert.Single(List(view, "warnings"), w => Str(w, "code") == "fx_missing");
        Assert.Equal("GBP", Str(warning, "detail"));
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

        var warning = Assert.Single(List(view, "warnings"), w => Str(w, "code") == "income_end_extended");
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

    [Fact]
    public async Task Projection_uses_the_observed_inflation_this_year() // inflation spec D5, C16
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedCpiAsync(new YearMonth(2025, 9), 3m);
        await ValuedEnvelopeAsync(app, app.AdultA, "10000.00");

        var view = await ProjectionAsync(app, app.AdultA);

        Assert.Equal("3.0", Str(view, "observedInflation"));
        // 2025-11 and 2025-12 at the observed 3 %, 2026 at the long-term 2 %.
        var i = ProjectionRates.Resolve(ProjectionDefaults.Assumptions, 0.03m, Today, 120, []).Inflation;
        DeflatedBy(i, List(view, "points"), 1, 2, 3, 120);
        Assert.NotEqual(i[2], ProjectionRates.Resolve(ProjectionDefaults.Assumptions, null, Today, 120, []).Inflation[2]);
    }

    [Fact]
    public async Task Non_eur_household_uses_the_long_term_inflation() // inflation spec C7
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedCpiAsync(new YearMonth(2025, 9), 3m);
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, "/api/v1/admin/household", new { referenceCurrency = "USD" })).EnsureSuccessStatusCode();
        await HouseholdSavingsAsync(app, "500");

        var view = await ProjectionAsync(app, app.AdultA);

        Assert.Equal(("USD", JsonValueKind.Null), (Str(view, "currency"), view.GetProperty("observedInflation").ValueKind));
        var i = Enumerable.Range(0, 121).Select(k => (decimal)Math.Pow(1.02, k / 12.0)).ToList();
        DeflatedBy(i, List(view, "points"), 2, 12, 120);
    }

    [Fact]
    public async Task Projection_without_series_uses_the_long_term_inflation()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await HouseholdSavingsAsync(app, "500");

        var view = await ProjectionAsync(app, app.AdultA);

        Assert.Equal(JsonValueKind.Null, view.GetProperty("observedInflation").ValueKind);
        var i = Enumerable.Range(0, 121).Select(k => (decimal)Math.Pow(1.02, k / 12.0)).ToList();
        DeflatedBy(i, List(view, "points"), 2, 12, 120);
    }

    [Fact]
    public async Task Future_phase_is_indexed_from_its_start() // inflation spec D8, C17
    {
        await using var app = await NidApp.StartAsync(fixture);
        await IncomeSourceAsync(app, "Salaire", "salary", "2020-01-01", "3000");
        await IncomeSourceAsync(app, "Prime", "other", "2045-01-01", "2000");
        var assumptions = ProjectionDefaults.Assumptions with { IncomeGrowth = 0.01m };

        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
        var read = await scope.ServiceProvider.GetRequiredService<ProjectionFactsReader>()
            .ReadAsync(VisibilityScope.Household, new ProjectionHorizon(20, null), assumptions, RealEstateEvents.None, TestContext.Current.CancellationToken);

        Assert.True(read.TryGetValue(out var reading), read.ToString());
        var g = ProjectionRates.Resolve(assumptions, null, Today, 240, []).Income;
        const int pension = 231; // 2045-01: the phase's first month, in euros of that month; the salary runs since before today
        Assert.Equal(Math.Round(3000m * g[pension - 1], 6), Math.Round(reading!.Inputs.Income[pension - 1], 6));
        Assert.Equal(Math.Round(3000m * g[pension] + 2000m, 6), Math.Round(reading.Inputs.Income[pension], 6));
        Assert.Equal(Math.Round(3000m * g[240] + 2000m * g[240] / g[pension], 6), Math.Round(reading.Inputs.Income[240], 6));
    }

    [Fact]
    public async Task A_pension_is_indexed_from_today() // retirement spec: pension parts are indexed from 0
    {
        await using var app = await NidApp.StartAsync(fixture);
        await IncomeSourceAsync(app, "Salaire", "salary", "2020-01-01", "3000");
        await IncomeSourceAsync(app, "Retraite", "pension", "2045-01-01", "2000");
        var assumptions = ProjectionDefaults.Assumptions with { IncomeGrowth = 0.01m };

        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
        var read = await scope.ServiceProvider.GetRequiredService<ProjectionFactsReader>()
            .ReadAsync(VisibilityScope.Household, new ProjectionHorizon(20, null), assumptions, RealEstateEvents.None, TestContext.Current.CancellationToken);

        Assert.True(read.TryGetValue(out var reading), read.ToString());
        var g = ProjectionRates.Resolve(assumptions, null, Today, 240, []).Income;
        const int pension = 231; // 2045-01: the pension's first month; both are in euros of today
        Assert.Equal(Math.Round(3000m * g[pension - 1], 6), Math.Round(reading!.Inputs.Income[pension - 1], 6));
        Assert.Equal(Math.Round(5000m * g[pension], 6), Math.Round(reading.Inputs.Income[pension], 6));
        Assert.Equal(Math.Round(5000m * g[240], 6), Math.Round(reading.Inputs.Income[240], 6));
    }

    /// <summary>Flat money (every class at 0 %, no inflation), saving nothing, a living cost of <paramref name="livingCost"/> a month, withdrawals at 4 %.</summary>
    private static async Task FlatAsync(NidApp app, string livingCost)
        => (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/core/projection-assumptions", new
        {
            classRates = AllocationClasses.All.ToDictionary(c => c, _ => "0"), inflation = "0", monthlySavings = "0", savingsClass = "cash",
            freedPaymentsToSavings = false, withdrawalRate = "4", monthlyLivingCost = livingCost,
        })).EnsureSuccessStatusCode();

    [Fact]
    public async Task Independence_is_this_month_when_the_capital_pays_the_living_cost() // retirement spec §3
    {
        await using var app = await NidApp.StartAsync(fixture);
        await FlatAsync(app, "1000");
        await ValuedEnvelopeAsync(app, app.AdultA, "600000"); // 600 000 × 4 % ÷ 12 = 2 000 a month

        Assert.Equal("2025-10", Str(await ProjectionAsync(app, app.AdultA, "?horizon=5y"), "financialIndependence"));
    }

    [Fact]
    public async Task Independence_is_null_when_the_living_cost_is_out_of_reach() // retirement spec §3
    {
        await using var app = await NidApp.StartAsync(fixture);
        await FlatAsync(app, "100000");
        await ValuedEnvelopeAsync(app, app.AdultA, "600000");

        Assert.Equal(JsonValueKind.Null, (await ProjectionAsync(app, app.AdultA, "?horizon=5y")).GetProperty("financialIndependence").ValueKind);
    }

    [Fact]
    public async Task A_pension_from_the_departure_moves_independence_to_that_month() // retirement spec §3
    {
        await using var app = await NidApp.StartAsync(fixture);
        await FlatAsync(app, "2000");
        await ValuedEnvelopeAsync(app, app.AdultA, "100000"); // 333 a month: not enough alone
        await RetirementAsync(app, app.AdultA, "1963-10-10"); // the departure month is 2027-10 (month 24), the switch month 25
        await IncomeSourceAsync(app, "Retraite", "pension", "2045-01-01", "2000");
        await IncomeSourceAsync(app, "Salaire", "salary", "2020-01-01", "3000"); // work income never counts as a pension

        var view = await ProjectionAsync(app, app.AdultA, "?horizon=5y");

        Assert.Equal("2027-11", Str(view, "financialIndependence"));
        Assert.Contains(List(view, "milestones"), m => (Str(m, "kind"), Str(m, "on")) == ("financial_independence", "2027-11-30"));
    }

    private static async Task<string?> IndependenceAsync(NidApp app, string livingCost)
    {
        await FlatAsync(app, livingCost);
        var view = await ProjectionAsync(app, app.AdultA, "?horizon=5y");
        return view.GetProperty("financialIndependence").GetString();
    }

    [Fact]
    public async Task Expenses_are_the_commitments_but_the_loans_and_the_loan_payments() // retirement spec §3: the loan counts once
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.NewMonthlyAsync(app.AdultA, 100m); // Expenses0 = 100
        await NewAsync(app, Short(app)); // 1 000 a month until 2025-12-15
        await ValuedEnvelopeAsync(app, app.AdultA, "330000"); // 330 000 × 4 % ÷ 12 = 1 100 a month

        // 1 100 are covered from the first month without a loan payment: 100 + 1 000 of living cost still fit, 1 001 do not.
        Assert.Equal("2026-01", await IndependenceAsync(app, "1000"));
        Assert.Null(await IndependenceAsync(app, "1001"));
    }

    [Fact]
    public async Task A_rent_is_passive_income() // retirement spec §3
    {
        await using var app = await NidApp.StartAsync(fixture);
        var property = await NewPropertyAsync(app, Home(app));
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{property}/tenancies",
            new { label = "Bail", startsOn = "2025-01-01", endsOn = (string?)null, rentMonthly = "800", chargesMonthly = "60" })); // charges are not income

        Assert.Equal("2025-10", await IndependenceAsync(app, "800"));
        Assert.Null(await IndependenceAsync(app, "801"));
    }

    [Fact]
    public async Task A_commitment_in_a_currency_without_a_rate_is_named()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var subscription = await app.NewSubscriptionAsync("Abonnement", app.AdultA, Visibility.Shared);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{subscription.Value}/phases", new
        {
            startsOn = "2025-01-01", price = new { amount = "10", currency = "GBP" }, every = new { count = 1, unit = "month" }, payer = new { type = "household" },
        })).EnsureSuccessStatusCode();

        var warning = Assert.Single(List(await ProjectionAsync(app, app.AdultA), "warnings"), w => Str(w, "code") == "fx_missing");

        Assert.Equal("GBP", Str(warning, "detail"));
    }
}
