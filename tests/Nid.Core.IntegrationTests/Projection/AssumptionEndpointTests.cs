using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Core.IntegrationTests.Projection;

/// <summary>Net worth projection spec §2.1: the household's assumptions, read and replaced by any adult.</summary>
public class AssumptionEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Url = "/api/v1/core/projection-assumptions";

    private static Dictionary<string, string> Rates(string equity) => new()
    {
        ["cash"] = "2", ["money_market"] = "2.5", ["bond"] = "3", ["equity"] = equity, ["mixed"] = "4",
        ["capital_guaranteed"] = "2.5", ["real_estate"] = "2", ["other"] = "0",
    };

    private static object Body(string equity) => new
    {
        classRates = Rates(equity), inflation = "2", monthlySavings = (string?)null, savingsClass = "cash", freedPaymentsToSavings = true,
    };

    [Fact]
    public async Task Get_returns_defaults_as_percent_strings()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var view = await app.GetAsync<JsonElement>(app.AdultA, Url);

        Assert.Equal("6", view.GetProperty("classRates").GetProperty("equity").GetString());
        Assert.Equal("2", view.GetProperty("inflation").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("monthlySavings").ValueKind);
        Assert.Equal("cash", view.GetProperty("savingsClass").GetString());
        Assert.True(view.GetProperty("freedPaymentsToSavings").GetBoolean());
        Assert.Equal("EUR", view.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task Any_adult_replaces_them()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var put = await app.RawAsync(app.AdultB, HttpMethod.Put, Url, new
        {
            classRates = Rates("7"), inflation = "2.5", monthlySavings = "500", savingsClass = "equity", freedPaymentsToSavings = false,
        });

        Assert.True(put.StatusCode == HttpStatusCode.OK, await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var view = await app.GetAsync<JsonElement>(app.AdultA, Url);
        Assert.Equal("7", view.GetProperty("classRates").GetProperty("equity").GetString());
        Assert.Equal("2.5", view.GetProperty("inflation").GetString());
        Assert.Equal("500.00", view.GetProperty("monthlySavings").GetString());
        Assert.Equal("equity", view.GetProperty("savingsClass").GetString());
        Assert.False(view.GetProperty("freedPaymentsToSavings").GetBoolean());
        Assert.Equal("EUR", view.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task Invalid_rate_names_its_field()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, Url, Body("51"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("projection.invalid_field", problem.GetProperty("code").GetString());
        Assert.Equal("classRates.equity", problem.GetProperty("errors")[0].GetProperty("path").GetString());
        Assert.Equal("6", (await app.GetAsync<JsonElement>(app.AdultA, Url)).GetProperty("classRates").GetProperty("equity").GetString());
    }

    [Fact]
    public async Task Scenario_defaults_follow_the_household_rates()
    {
        await using var app = await NidApp.StartAsync(fixture);
        (await app.RawAsync(app.AdultA, HttpMethod.Put, Url, Body("7"))).EnsureSuccessStatusCode();

        var defaults = await app.GetAsync<JsonElement>(app.AdultA, "/api/v1/scenarios/defaults");

        Assert.Equal("7", defaults.GetProperty("classRates").GetProperty("equity").GetString());
    }

    private static object Yearly(object? inflationByYear = null, object? classRatesByYear = null, string? incomeGrowth = null) => new
    {
        classRates = Rates("6"), inflation = "2", monthlySavings = (string?)null, savingsClass = "cash", freedPaymentsToSavings = true,
        inflationByYear, classRatesByYear, incomeGrowth,
    };

    [Fact]
    public async Task Put_then_get_round_trips_the_yearly_tables() // inflation spec C15
    {
        await using var app = await NidApp.StartAsync(fixture);

        var put = await app.RawAsync(app.AdultA, HttpMethod.Put, Url,
            Yearly(new Dictionary<string, string> { ["2025"] = "2.5", ["2026"] = "2.2" }, new { cash = new Dictionary<string, string> { ["2026"] = "3" } }, "0.5"));

        Assert.True(put.StatusCode == HttpStatusCode.OK, await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var view = await app.GetAsync<JsonElement>(app.AdultA, Url);
        Assert.Equal("""{"2025":"2.5","2026":"2.2"}""", view.GetProperty("inflationByYear").GetRawText());
        Assert.Equal("""{"cash":{"2026":"3"}}""", view.GetProperty("classRatesByYear").GetRawText());
        Assert.Equal("0.5", view.GetProperty("incomeGrowth").GetString());
    }

    [Fact]
    public async Task Saving_drops_past_years()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var put = await app.RawAsync(app.AdultA, HttpMethod.Put, Url, Yearly(
            new Dictionary<string, string> { ["2024"] = "9", ["2026"] = "2.2" }, new { cash = new Dictionary<string, string> { ["2024"] = "9" } }));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var view = await app.GetAsync<JsonElement>(app.AdultA, Url);
        Assert.Equal("""{"2026":"2.2"}""", view.GetProperty("inflationByYear").GetRawText());
        Assert.Equal("{}", view.GetProperty("classRatesByYear").GetRawText());
    }

    [Fact]
    public async Task Saving_old_assumptions_round_trips()
    {
        await using var app = await NidApp.StartAsync(fixture);

        (await app.RawAsync(app.AdultA, HttpMethod.Put, Url, Body("6"))).EnsureSuccessStatusCode();

        var view = await app.GetAsync<JsonElement>(app.AdultA, Url);
        Assert.Equal("{}", view.GetProperty("inflationByYear").GetRawText());
        Assert.Equal("{}", view.GetProperty("classRatesByYear").GetRawText());
        Assert.Equal("0", view.GetProperty("incomeGrowth").GetString());
    }

    [Fact]
    public async Task Get_gives_the_observed_inflation_hint()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedCpiAsync(new NodaTime.YearMonth(2025, 9), 3m);

        var view = await app.GetAsync<JsonElement>(app.AdultA, Url);

        Assert.Equal("""{"to":"2025-09","percent":"3.0"}""", view.GetProperty("observedInflation").GetRawText());
    }

    private sealed class December : NodaTime.IClock
    {
        public NodaTime.Instant GetCurrentInstant() => NodaTime.Instant.FromUtc(2025, 12, 15, 12, 0);
    }

    private static async Task StoreAsync(NidApp app, ProjectionAssumptions assumptions)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        await scope.ServiceProvider.GetRequiredService<IHouseholdSettings>().SaveProjectionAssumptionsAsync(assumptions, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_stored_past_year_is_ignored_when_read() // inflation spec C15
    {
        await using var app = await NidApp.StartAsync(fixture);
        await StoreAsync(app, ProjectionDefaults.Assumptions with { InflationByYear = new Dictionary<int, decimal> { [2024] = 0.09m, [2026] = 0.025m } });

        var view = await app.GetAsync<JsonElement>(app.AdultA, Url);
        var projection = await app.GetAsync<JsonElement>(app.AdultA, "/api/v1/projection");

        Assert.Equal("""{"2026":"2.5"}""", view.GetProperty("inflationByYear").GetRawText());
        Assert.Equal("""{"2026":"2.5"}""", projection.GetProperty("assumptions").GetProperty("inflationByYear").GetRawText());
    }

    [Fact]
    public async Task December_gives_no_observed_hint_anywhere() // the projection no longer follows it
    {
        await using var app = await NidApp.StartAsync(fixture, s => s.AddSingleton<NodaTime.IClock>(new December()));
        await app.SeedCpiAsync(new NodaTime.YearMonth(2025, 9), 3m);

        var view = await app.GetAsync<JsonElement>(app.AdultA, Url);
        var projection = await app.GetAsync<JsonElement>(app.AdultA, "/api/v1/projection");

        Assert.Equal(JsonValueKind.Null, view.GetProperty("observedInflation").ValueKind);
        Assert.Equal(JsonValueKind.Null, projection.GetProperty("observedInflation").ValueKind);
    }

    [Theory]
    [InlineData("""{"abc":"2"}""", "inflationByYear.abc")]
    [InlineData("""{"2031":"2"}""", "inflationByYear.2031")]
    [InlineData("""{"02026":"2"}""", "inflationByYear.02026")]
    [InlineData("""{"abc":"x"}""", "inflationByYear.abc")]
    [InlineData("""{"2026":"25"}""", "inflationByYear.2026")]
    public async Task Put_refuses_bad_yearly_values(string inflationByYear, string path)
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, Url,
            Yearly(JsonSerializer.Deserialize<JsonElement>(inflationByYear)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var errors = problem.GetProperty("errors");
        Assert.Equal(path, errors[0].GetProperty("path").GetString());
        Assert.Equal(1, errors.GetArrayLength()); // a bad key is refused once, its value unread
    }

    [Fact]
    public async Task Put_refuses_a_yearly_rate_for_a_class_without_one()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, Url,
            Yearly(classRatesByYear: new { equity = new Dictionary<string, string> { ["2026"] = "3" } }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("classRatesByYear.equity", problem.GetProperty("errors")[0].GetProperty("path").GetString());
    }
}
