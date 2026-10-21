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
