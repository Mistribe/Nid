using System.Globalization;
using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.TaxEstimate;

/// <summary>Tax estimate spec §6 to §8, through the real API.</summary>
public class TaxEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<JsonElement> EstimateAsync(NidApp app, PersonId reader, int year, string scope = "me")
        => JsonDocument.Parse(await app.GetRawAsync(reader, $"/api/v1/tax?year={year}&scope={scope}")).RootElement;

    private static async Task AddPhaseAsync(NidApp app, PersonId owner, ItemId source, string net, string gross)
        => (await app.RawAsync(owner, HttpMethod.Post, $"/api/v1/income/sources/{source.Value}/phases", new
        {
            label = "Salaire", startsOn = "2025-01-01", netAmount = new { amount = net, currency = "EUR" },
            every = new { count = 1, unit = "year" }, grossAmount = new { amount = gross, currency = "EUR" },
        })).EnsureSuccessStatusCode();

    private static decimal GrossOf(JsonElement estimate)
        => estimate.GetProperty("inputs").GetProperty("declarants").EnumerateArray()
            .Sum(d => decimal.Parse(d.GetProperty("salaryGross").GetString()!, CultureInfo.InvariantCulture));

    private static async Task<string> PerAsync(NidApp app, PersonId owner, string name, string openedOn, string openingOn, string visibility = "shared")
        => JsonDocument.Parse(await (await app.RawAsync(owner, HttpMethod.Post, "/api/v1/investments/envelopes", new
        {
            displayName = name, kind = "per", currency = "EUR", visibility, openedOn,
            owners = new[] { new { personId = owner.Value, shareBp = 10_000 } },
            opening = new { occurredOn = openingOn, amount = new { amount = "1000.00", currency = "EUR" } },
        })).EnsureSuccessStatusCode().Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;

    /// <summary>Each declarant's PER payments, by adult: the declarants come in the order of <c>adults</c>.</summary>
    private static Dictionary<Guid, string> PerOf(JsonElement estimate)
        => estimate.GetProperty("adults").EnumerateArray().Zip(estimate.GetProperty("inputs").GetProperty("declarants").EnumerateArray())
            .ToDictionary(p => p.First.GetProperty("id").GetGuid(), p => p.Second.GetProperty("perContributions").GetString()!);

    [Fact]
    public async Task The_gross_of_the_year_feeds_the_estimate_and_the_other_adults_private_salary_stays_out()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await app.NewIncomeSourceAsync("Salaire A", app.AdultA);
        await AddPhaseAsync(app, app.AdultA, mine, "39000", "50000");
        var theirs = await app.NewIncomeSourceAsync("Salaire B", app.AdultB, visibility: "private");
        await AddPhaseAsync(app, app.AdultB, theirs, "30000", "40000");

        var estimate = await EstimateAsync(app, app.AdultA, 2025);

        Assert.Equal(50_000m, GrossOf(estimate));
        Assert.Equal(90_000m, GrossOf(await EstimateAsync(app, app.AdultB, 2025)));
        Assert.DoesNotContain("Salaire B", estimate.GetRawText());
        Assert.Equal(2025, estimate.GetProperty("result").GetProperty("rulesYear").GetInt32());
    }

    [Fact]
    public async Task Every_PER_payment_of_the_year_is_deducted_the_opening_of_a_PER_opened_that_year_included()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var old = await PerAsync(app, app.AdultA, "PER ancien", "2020-03-01", "2025-01-10"); // tracked from 2025: its opening is older payments
        await PerAsync(app, app.AdultA, "PER neuf", "2025-02-01", "2025-02-01");
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{old}/flows",
            new { occurredOn = "2025-06-15", kind = "contribution", amount = new { amount = "500.00", currency = "EUR" } })).EnsureSuccessStatusCode();

        Assert.Equal("1500.00", PerOf(await EstimateAsync(app, app.AdultA, 2025))[app.AdultA.Value]);
    }

    [Fact]
    public async Task Each_adults_shared_PER_goes_to_its_owner_in_both_scopes_and_a_private_one_only_to_its_owner()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await PerAsync(app, app.AdultA, "PER A", "2025-02-01", "2025-02-01");
        await PerAsync(app, app.AdultB, "PER B", "2025-03-01", "2025-03-01");
        await PerAsync(app, app.AdultB, "PER B privé", "2025-04-01", "2025-04-01", visibility: "private");

        foreach (var scope in new[] { "household", "me" })
        {
            var seenByA = PerOf(await EstimateAsync(app, app.AdultA, 2025, scope));
            Assert.Equal("1000.00", seenByA[app.AdultA.Value]);
            Assert.Equal("1000.00", seenByA[app.AdultB.Value]);
        }
        Assert.Equal("2000.00", PerOf(await EstimateAsync(app, app.AdultB, 2025))[app.AdultB.Value]);
    }

    [Fact]
    public async Task A_saved_profile_gives_its_credits_and_stands_in_for_the_next_year()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await app.NewIncomeSourceAsync("Salaire", app.AdultA);
        await AddPhaseAsync(app, app.AdultA, source, "39000", "50000");

        var saved = await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/tax/profile/2025?scope=me", new
        {
            dependents = 2, childrenUnder6 = 2, childcareOutsideHome = "6000", pinel = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var result = JsonDocument.Parse(await saved.Content.ReadAsStringAsync()).RootElement.GetProperty("result");
        Assert.Equal("3000.00", result.GetProperty("childcareCredit").GetString());
        Assert.Equal("3.0", result.GetProperty("parts").GetString());

        var next = await EstimateAsync(app, app.AdultB, 2026, "household");
        Assert.Equal(2025, next.GetProperty("profileFrom").GetInt32());
        Assert.Equal("6000.00", next.GetProperty("profile").GetProperty("childcareOutsideHome").GetString());
    }

    [Fact]
    public async Task A_profile_out_of_bounds_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/tax/profile/2025", new
        {
            childrenUnder6 = 0, pinel = new[] { new { property = Guid.NewGuid(), initialYears = 12, totalYears = 12, startYear = (int?)null, plus = false } },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/tax?year=2024", null)).StatusCode);
    }

    [Fact]
    public async Task A_simulation_computes_given_inputs_and_checks_them()
    {
        await using var app = await NidApp.StartAsync(fixture);
        object Body(string status) => new
        {
            year = 2025, dependents = 2, childrenUnder6 = 2, childcareOutsideHome = "6000",
            declarants = new[]
            {
                new { name = "A", salaryGross = "50000", status = "non_executive", otherTaxable = "0", perContributions = "0" },
                new { name = "B", salaryGross = "40000", status, otherTaxable = "0", perContributions = "0" },
            },
        };

        var computed = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/tax/simulate", Body("executive"));
        Assert.Equal(HttpStatusCode.OK, computed.StatusCode);
        var result = JsonDocument.Parse(await computed.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("247.00", result.GetProperty("incomeTax").GetString()); // TaxCalculatorTests' first example
        Assert.Equal("64407.00", result.GetProperty("netTaxableIncome").GetString());
        Assert.Equal("4.5", result.GetProperty("withholding").GetProperty("rate").GetString());

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/tax/simulate", Body("boss"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("declarants[1].status", await refused.Content.ReadAsStringAsync());
    }
}
