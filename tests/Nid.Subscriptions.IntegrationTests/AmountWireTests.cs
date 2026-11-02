using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests;

/// <summary>
/// Amounts cross the HTTP API as decimal strings, both ways (frontend/README.md §2, principle 1): a JSON
/// number is read by a browser as a binary double, and 0.1 + 0.2 is where a household's totals start lying.
/// </summary>
public class AmountWireTests(PostgresFixture fixture, SharedNidApp shared) : IClassFixture<PostgresFixture>, IClassFixture<SharedNidApp>
{
    [Fact]
    public async Task Amounts_leave_the_api_as_strings_with_their_scale()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSpotifyGoldenCaseAsync(app.AdultA);

        var detail = await app.GetAsync<JsonElement>(app.AdultA, $"/api/v1/subscriptions/{id.Value}");

        var next = detail.GetProperty("nextChargeAmount").GetProperty("amount");
        Assert.Equal(JsonValueKind.String, next.ValueKind);
        Assert.Equal("17.20", next.GetString());
        Assert.All(detail.GetProperty("charges").EnumerateArray(),
            c => Assert.Equal(JsonValueKind.String, c.GetProperty("price").GetProperty("amount").ValueKind));
    }

    [Fact]
    public async Task Household_totals_are_strings_too()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.NewMonthlyAsync(app.AdultA, 9.99m);

        var list = await app.GetAsync<JsonElement>(app.AdultA, "/api/v1/subscriptions");

        Assert.All(list.GetProperty("householdCashCost").EnumerateArray(),
            m => Assert.Equal(JsonValueKind.String, m.GetProperty("amount").ValueKind));
    }

    [Fact]
    public async Task A_json_number_for_an_amount_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("numbers", app.AdultA, Visibility.Shared);

        var response = await app.RawTextAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases",
            """{"startsOn":"2025-01-01","price":{"amount":9.99,"currency":"EUR"},"every":{"count":1,"unit":"month"},"payer":{"type":"household"}}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("9,99")]
    [InlineData("1e3")]
    [InlineData("")]
    [InlineData("12.34567")] // more than the four decimals numeric(19,4) keeps
    public async Task An_unreadable_amount_string_is_refused(string amount)
    {
        var app = shared.App;
        var id = await app.NewSubscriptionAsync("strings", app.AdultA, Visibility.Shared);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn = "2025-01-01",
            price = new { amount, currency = "EUR" },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("request.malformed", problem.GetProperty("code").GetString());
    }
}
