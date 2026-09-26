using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests;

/// <summary>1E2a design §2.1 and §2.2: one request creates a subscription with its first phase, or nothing.</summary>
[Collection("postgres")]
public class CreateWithPhaseTests(PostgresFixture fixture)
{
    private static object Body(NidApp app, object? phase, string visibility = "shared", string? providerKey = null) => new
    {
        displayName = "Netflix",
        visibility,
        owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
        providerKey,
        autoRenew = true,
        phases = phase is null ? null : new[] { phase },
    };

    private static object Phase(string amount = "13.49", string startsOn = "2025-01-01", string? endsOn = null, string unit = "month") => new
    {
        startsOn, endsOn, price = new { amount, currency = "EUR" }, every = new { count = 1, unit }, payer = new { type = "household" },
    };

    /// <summary>The test server's body can be read once: every assertion reads this.</summary>
    private static async Task<JsonElement> ProblemOf(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static List<(string Path, string Code)> ErrorsOf(JsonElement problem)
        => problem.GetProperty("errors").EnumerateArray()
            .Select(e => (e.GetProperty("path").GetString()!, e.GetProperty("code").GetString()!)).ToList();

    [Fact]
    public async Task A_subscription_and_its_first_phase_are_created_by_one_request()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions", Body(app, Phase()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var detail = await app.GetAsync<JsonElement>(app.AdultA, $"/api/v1/subscriptions/{id}");
        Assert.Equal("13.49", detail.GetProperty("nextChargeAmount").GetProperty("amount").GetString());
    }

    [Fact]
    public async Task Shape_errors_come_back_by_path_all_at_once()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions",
            Body(app, Phase(amount: "9,99", startsOn: "2025-02-30", unit: "fortnight"), visibility: "secret"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ProblemOf(response);
        Assert.Equal("request.invalid", problem.GetProperty("code").GetString());
        Assert.Equal(
            ["phases[0].every.unit", "phases[0].price.amount", "phases[0].startsOn", "visibility"],
            ErrorsOf(problem).Select(e => e.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_refused_phase_creates_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        int items;
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
            items = await core.Items.CountAsync();

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions",
            Body(app, Phase(startsOn: "2025-03-01", endsOn: "2025-02-01")));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal([("phases[0].endsOn", "subscriptions.phase_range")], ErrorsOf(await ProblemOf(response)));
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
            Assert.Equal(items, await core.Items.CountAsync());
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        Assert.False(await subs.Subscriptions.AnyAsync());
    }

    [Fact]
    public async Task A_rule_refusal_keeps_its_own_code_and_gains_its_path()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions", Body(app, null, providerKey: "no-such-provider"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ProblemOf(response);
        Assert.Equal("subscriptions.unknown_provider", problem.GetProperty("code").GetString());
        Assert.Equal([("providerKey", "subscriptions.unknown_provider")], ErrorsOf(problem));
    }

    [Fact]
    public async Task Owner_shares_short_of_the_whole_are_refused_at_owners()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions", new
        {
            displayName = "Half", visibility = "shared", autoRenew = true,
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 5_000 } },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("owners", Assert.Single(ErrorsOf(await ProblemOf(response))).Path);
    }
}
