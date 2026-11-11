using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nid.TestSupport;

namespace Nid.Core.IntegrationTests.Projection;

/// <summary>#155: the guided projection settings, one card at a time (PATCH), the market profile derived and its effect served.</summary>
public class AssumptionPatchEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Url = "/api/v1/core/projection-assumptions";

    private static async Task<JsonElement> PatchAsync(NidApp app, object body)
    {
        var response = await app.RawAsync(app.AdultB, HttpMethod.Patch, Url, body);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<JsonElement> RefusedAsync(NidApp app, object body)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Patch, Url, body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("errors");
    }

    [Fact]
    public async Task Get_derives_the_profile_and_serves_the_defaults()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var view = await app.GetAsync<JsonElement>(app.AdultA, Url);

        Assert.Equal("balanced", view.GetProperty("profile").GetString());
        var defaults = view.GetProperty("defaults");
        Assert.Equal(("6", "16"), (defaults.GetProperty("classRates").GetProperty("equity").GetString(), defaults.GetProperty("classVolatility").GetProperty("equity").GetString()));
        Assert.Equal(("2", "0", "4"), (defaults.GetProperty("inflation").GetString(), defaults.GetProperty("incomeGrowth").GetString(), defaults.GetProperty("withdrawalRate").GetString()));
    }

    [Fact]
    public async Task A_partial_patch_keeps_every_other_value()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await PatchAsync(app, new { withdrawalRate = "3.5", classRates = new { equity = "7" } });

        var view = await PatchAsync(app, new { monthlyLivingCost = "2500" });

        Assert.Equal("2500.00", view.GetProperty("monthlyLivingCost").GetString());
        Assert.Equal("3.5", view.GetProperty("withdrawalRate").GetString());
        Assert.Equal(("7", "2"), (view.GetProperty("classRates").GetProperty("equity").GetString(), view.GetProperty("classRates").GetProperty("cash").GetString()));
        Assert.Equal("custom", view.GetProperty("profile").GetString());
        Assert.Equal(("2", "cash"), (view.GetProperty("inflation").GetString(), view.GetProperty("savingsClass").GetString()));
    }

    [Fact]
    public async Task A_profile_sets_every_rate_and_volatility_and_keeps_the_yearly_rates()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await PatchAsync(app, new { classVolatility = new { equity = "20" }, classRatesByYear = new { cash = new Dictionary<string, string> { ["2026"] = "3" } } });

        var view = await PatchAsync(app, new { profile = "optimistic" });

        Assert.Equal("optimistic", view.GetProperty("profile").GetString());
        Assert.Equal(("8", "2.5"), (view.GetProperty("classRates").GetProperty("equity").GetString(), view.GetProperty("classRates").GetProperty("cash").GetString()));
        Assert.Equal("16", view.GetProperty("classVolatility").GetProperty("equity").GetString());
        Assert.Equal("""{"cash":{"2026":"3"}}""", view.GetProperty("classRatesByYear").GetRawText());
    }

    [Fact]
    public async Task Savings_go_to_an_amount_then_back_to_automatic()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var fixedView = await PatchAsync(app, new { monthlySavings = "500", savingsClass = "equity", freedPaymentsToSavings = false });
        var auto = await PatchAsync(app, new { monthlySavings = "auto" });

        Assert.Equal(("500.00", "equity", false), (fixedView.GetProperty("monthlySavings").GetString(), fixedView.GetProperty("savingsClass").GetString(), fixedView.GetProperty("freedPaymentsToSavings").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, auto.GetProperty("monthlySavings").ValueKind);
        Assert.Equal(("equity", false), (auto.GetProperty("savingsClass").GetString(), auto.GetProperty("freedPaymentsToSavings").GetBoolean()));
    }

    [Fact]
    public async Task Yearly_class_rates_merge_class_by_class_and_an_empty_table_clears_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await PatchAsync(app, new { classRatesByYear = new { cash = new Dictionary<string, string> { ["2026"] = "3" } } });

        var merged = await PatchAsync(app, new { classRatesByYear = new { money_market = new Dictionary<string, string> { ["2027"] = "2.8" } } });
        var cleared = await PatchAsync(app, new { classRatesByYear = new { cash = new Dictionary<string, string>() } });

        Assert.Equal("""{"cash":{"2026":"3"},"money_market":{"2027":"2.8"}}""", merged.GetProperty("classRatesByYear").GetRawText());
        Assert.Equal("""{"money_market":{"2027":"2.8"}}""", cleared.GetProperty("classRatesByYear").GetRawText());
    }

    [Fact]
    public async Task Patches_at_once_on_different_fields_all_survive() // one card saving while another does
    {
        await using var app = await NidApp.StartAsync(fixture);

        await Task.WhenAll(
            PatchAsync(app, new { savingsClass = "equity" }), PatchAsync(app, new { freedPaymentsToSavings = false }),
            PatchAsync(app, new { profile = "prudent" }), PatchAsync(app, new { incomeGrowth = "1" }),
            PatchAsync(app, new { withdrawalRate = "3.5" }), PatchAsync(app, new { monthlyLivingCost = "2000" }),
            PatchAsync(app, new { inflation = "2.5" }), PatchAsync(app, new { monthlySavings = "300" }));

        var view = await app.GetAsync<JsonElement>(app.AdultA, Url);
        Assert.Equal(("equity", false, "prudent", "1"), (view.GetProperty("savingsClass").GetString(), view.GetProperty("freedPaymentsToSavings").GetBoolean(),
            view.GetProperty("profile").GetString(), view.GetProperty("incomeGrowth").GetString()));
        Assert.Equal(("3.5", "2000.00", "2.5", "300.00"), (view.GetProperty("withdrawalRate").GetString(), view.GetProperty("monthlyLivingCost").GetString(),
            view.GetProperty("inflation").GetString(), view.GetProperty("monthlySavings").GetString()));
    }

    [Fact]
    public async Task A_profile_with_rates_is_refused_at_profile()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var errors = await RefusedAsync(app, new { profile = "prudent", classRates = new { equity = "7" } });

        Assert.Equal("profile", errors[0].GetProperty("path").GetString());
        Assert.Equal("balanced", (await app.GetAsync<JsonElement>(app.AdultA, Url)).GetProperty("profile").GetString());
    }

    [Fact]
    public async Task An_unknown_profile_is_refused_at_profile()
    {
        await using var app = await NidApp.StartAsync(fixture);

        Assert.Equal("profile", (await RefusedAsync(app, new { profile = "reckless" }))[0].GetProperty("path").GetString());
    }

    [Theory]
    [InlineData("""{"classRates":{"equity":"51"}}""", "classRates.equity")]
    [InlineData("""{"withdrawalRate":"0"}""", "withdrawalRate")]
    [InlineData("""{"inflationByYear":{"2031":"2"}}""", "inflationByYear.2031")]
    [InlineData("""{"monthlySavings":"lots"}""", "monthlySavings")]
    public async Task A_refusal_keeps_its_field_path(string body, string path)
    {
        await using var app = await NidApp.StartAsync(fixture);

        var errors = await RefusedAsync(app, JsonSerializer.Deserialize<JsonElement>(body));

        Assert.Equal(path, errors[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Profiles_give_their_rates_and_their_effect_in_ten_years()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await PatchAsync(app, new { monthlySavings = "500", savingsClass = "equity" });

        var view = await app.GetAsync<JsonElement>(app.AdultA, $"{Url}/profiles");

        Assert.Equal("EUR", view.GetProperty("currency").GetString());
        var profiles = view.GetProperty("profiles").EnumerateArray().ToList();
        Assert.Equal(["prudent", "balanced", "optimistic"], profiles.Select(p => p.GetProperty("key").GetString()));
        Assert.Equal(["4", "6", "8"], profiles.Select(p => p.GetProperty("classRates").GetProperty("equity").GetString()));
        Assert.All(profiles, p => Assert.Equal("16", p.GetProperty("classVolatility").GetProperty("equity").GetString()));
        var inTenYears = profiles.Select(p => decimal.Parse(p.GetProperty("inTenYears").GetString()!, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.True(inTenYears[0] < inTenYears[1] && inTenYears[1] < inTenYears[2], string.Join(" < ", inTenYears));
    }
}
