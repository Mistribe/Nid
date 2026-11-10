using System.Globalization;
using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Core.Domain.Projection;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using static Nid.TestSupport.ProjectionApi;

namespace Nid.Core.IntegrationTests.Projection;

/// <summary>Retirement spec §3 and §4: <c>GET /api/v1/retirement</c>. Today is 2025-10-15; flat money, so today's money is the nominal one.</summary>
public class RetirementEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<JsonElement> RetirementAsync(NidApp app, PersonId reader, string query = "")
    {
        var response = await app.RawAsync(reader, HttpMethod.Get, "/api/v1/retirement" + query);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task DepartureAsync(NidApp app, PersonId person, string birthDate)
        => (await app.RawAsync(app.AdultA, HttpMethod.Put, $"/api/v1/core/persons/{person.Value}/retirement", new { birthDate, retirementAge = 64, retirementMonths = 0 }))
            .EnsureSuccessStatusCode();

    private static async Task FlatAsync(NidApp app)
        => (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/core/projection-assumptions", new
        {
            classRates = AllocationClasses.All.ToDictionary(c => c, _ => "0"), inflation = "0", monthlySavings = "0", savingsClass = "cash",
            freedPaymentsToSavings = false, withdrawalRate = "4", monthlyLivingCost = "1000",
        })).EnsureSuccessStatusCode();

    private static async Task SourceAsync(NidApp app, PersonId owner, string name, string kind, string startsOn, string amount, string visibility)
        => (await app.RawAsync(owner, HttpMethod.Post, "/api/v1/income/sources", new Dictionary<string, object?>
        {
            ["displayName"] = name, ["kind"] = kind, ["currency"] = "EUR", ["visibility"] = visibility,
            ["owners"] = new[] { new { personId = owner.Value, shareBp = 10_000 } },
            ["phases"] = new[] { new { label = name, startsOn, netAmount = new { amount, currency = "EUR" }, every = new { count = 1, unit = "month" } } },
        })).EnsureSuccessStatusCode();

    private static decimal Dec(JsonElement element, string name) => decimal.Parse(element.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);

    private async Task<NidApp> HouseholdAsync()
    {
        var app = await NidApp.StartAsync(fixture);
        await FlatAsync(app);
        await DepartureAsync(app, app.AdultA, "1963-10-10"); // leaves in 2027-10, switch month 25
        await DepartureAsync(app, app.AdultB, "1966-06-10"); // leaves in 2030-06, switch month 57
        await SourceAsync(app, app.AdultA, "Salaire A", "salary", "2020-01-01", "3000", "shared");
        await SourceAsync(app, app.AdultB, "Salaire B", "salary", "2020-01-01", "2000", "shared");
        await SourceAsync(app, app.AdultA, "Retraite A", "pension", "2045-01-01", "1500", "shared");
        await SourceAsync(app, app.AdultB, "Retraite B", "pension", "2045-01-01", "800", "private");
        return app;
    }

    [Fact]
    public async Task Household_figures_stop_at_the_last_departure_and_hide_a_private_pension()
    {
        await using var app = await HouseholdAsync();

        var view = await RetirementAsync(app, app.AdultA);

        var departures = view.GetProperty("departures").EnumerateArray().ToList();
        Assert.Equal(2, departures.Count);
        var a = departures.Single(d => d.GetProperty("person").GetGuid() == app.AdultA.Value);
        var b = departures.Single(d => d.GetProperty("person").GetGuid() == app.AdultB.Value);
        Assert.Equal(("2027-10", "1500.00"), (a.GetProperty("departure").GetString(), a.GetProperty("pension").GetString()));
        var source = Assert.Single(a.GetProperty("sources").EnumerateArray());
        Assert.Equal(("Retraite A", "1500.00", "EUR"), (source.GetProperty("name").GetString(), source.GetProperty("monthly").GetString(), source.GetProperty("currency").GetString()));
        Assert.Equal("2030-06", b.GetProperty("departure").GetString());
        Assert.Empty(b.GetProperty("sources").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, b.GetProperty("pension").ValueKind);

        var figures = view.GetProperty("figures");
        Assert.Equal("2030-07", figures.GetProperty("month").GetString()); // B leaves last: the switch month is 57
        Assert.Equal((5000m, 1500m, 3500m, 1_050_000m), (Dec(figures, "incomeNow"), Dec(figures, "incomeAtRetirement"), Dec(figures, "gap"), Dec(figures, "capitalNeeded")));
        Assert.Equal(481, view.GetProperty("independence").GetArrayLength());

        // The private pension is hidden in the reader's own scope too.
        var mine = await RetirementAsync(app, app.AdultA, "?scope=me");
        var mineB = mine.GetProperty("departures").EnumerateArray().Single(d => d.GetProperty("person").GetGuid() == app.AdultB.Value);
        Assert.Empty(mineB.GetProperty("sources").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, mineB.GetProperty("pension").ValueKind);
        Assert.Equal(3500m, Dec(mine.GetProperty("figures"), "incomeAtRetirement")); // A's pension and B's salary, no 800
    }

    [Fact]
    public async Task Scope_me_uses_the_readers_own_departure()
    {
        await using var app = await HouseholdAsync();

        var mine = (await RetirementAsync(app, app.AdultA, "?scope=me")).GetProperty("figures"); // A leaves first: the switch month is 25
        Assert.Equal("2027-11", mine.GetProperty("month").GetString());
        Assert.Equal((5000m, 3500m, 1500m), (Dec(mine, "incomeNow"), Dec(mine, "incomeAtRetirement"), Dec(mine, "gap")));

        // B sees their own private pension on top of A's shared one.
        var view = await RetirementAsync(app, app.AdultB, "?scope=me");
        var theirs = view.GetProperty("figures");
        Assert.Equal(("2030-07", 2300m, 2700m), (theirs.GetProperty("month").GetString(), Dec(theirs, "incomeAtRetirement"), Dec(theirs, "gap")));
        Assert.Equal("800.00", view.GetProperty("departures").EnumerateArray().Single(d => d.GetProperty("person").GetGuid() == app.AdultB.Value).GetProperty("pension").GetString());
    }

    [Fact]
    public async Task Without_a_living_cost_the_page_is_warned_and_independence_is_unanswered()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await DepartureAsync(app, app.AdultA, "1963-10-10");
        await SourceAsync(app, app.AdultA, "Salaire A", "salary", "2020-01-01", "3000", "shared");

        var view = await RetirementAsync(app, app.AdultA);

        Assert.Equal(JsonValueKind.Null, view.GetProperty("financialIndependence").ValueKind);
        Assert.Equal(3000m, Dec(view.GetProperty("figures"), "gap"));
        Assert.Contains(view.GetProperty("warnings").EnumerateArray(), w => w.GetProperty("code").GetString() == "living_cost_missing");
        Assert.Equal(481, view.GetProperty("independence").GetArrayLength());
    }

    [Fact]
    public async Task A_reader_without_a_departure_has_no_figures_but_an_independence_month()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await FlatAsync(app);
        await DepartureAsync(app, app.AdultA, "1963-10-10");
        var envelope = await app.NewEnvelopeAsync("Livret", app.AdultB, Visibility.Private);
        Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(app.AdultB, HttpMethod.Post, $"/api/v1/investments/envelopes/{envelope.Value}/valuations",
            new { asOf = "2025-09-30", amount = new { amount = "400000", currency = "EUR" }, note = (string?)null })).StatusCode); // 1 333 a month

        var view = await RetirementAsync(app, app.AdultB, "?scope=me");

        Assert.Equal(JsonValueKind.Null, view.GetProperty("figures").ValueKind);
        var listed = view.GetProperty("departures").EnumerateArray().Single(d => d.GetProperty("person").GetGuid() == app.AdultB.Value);
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("departure").ValueKind);
        Assert.Equal("2025-10", view.GetProperty("financialIndependence").GetString()); // the capital covers the living cost today
    }
}
