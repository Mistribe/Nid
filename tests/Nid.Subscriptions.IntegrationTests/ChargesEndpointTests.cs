using System.Globalization;
using System.Net;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests;

/// <summary>GET /subscriptions/charges (the app clock is fixed at 2025-10-15).</summary>
public class ChargesEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    public sealed record Money(string Amount, string Currency);
    public sealed record Finding(string Code, string Message);
    public sealed record Line(string Date, Guid ItemId, string DisplayName, Money Price, Money? Converted);
    public sealed record MonthResult(string Month, Money? Total, List<Line> Charges, List<Finding> Findings);

    private static async Task<ItemId> MonthlyAsync(NidApp app, PersonId who, string name, string amount, Visibility visibility)
    {
        var id = await app.NewSubscriptionAsync(name, who, visibility);
        (await app.RawAsync(who, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn = "2025-01-01",
            price = new { amount, currency = "EUR" },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        return id;
    }

    [Fact]
    public async Task The_current_month_by_default()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await MonthlyAsync(app, app.AdultA, "Netflix", "10.00", Visibility.Shared);

        var result = await app.GetAsync<MonthResult>(app.AdultA, "/api/v1/subscriptions/charges");

        Assert.Equal("2025-10", result.Month);
        Assert.Equal("2025-10-01", Assert.Single(result.Charges).Date);
        Assert.Equal(10m, decimal.Parse(result.Total!.Amount, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_given_month()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await MonthlyAsync(app, app.AdultA, "Netflix", "10.00", Visibility.Shared);

        var result = await app.GetAsync<MonthResult>(app.AdultA, "/api/v1/subscriptions/charges?month=2025-11");

        Assert.Equal("2025-11", result.Month);
        Assert.Equal("2025-11-01", Assert.Single(result.Charges).Date);
    }

    [Fact]
    public async Task A_malformed_month_is_a_400()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/subscriptions/charges?month=2025-13");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_me_scope_hides_the_other_adults_private_subscription()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var secret = await MonthlyAsync(app, app.AdultB, "SecretTherapy", "30.00", Visibility.Private);
        await MonthlyAsync(app, app.AdultB, "SharedGym", "20.00", Visibility.Shared);

        const string path = "/api/v1/subscriptions/charges?scope=me&month=2025-11";
        var raw = await app.GetRawAsync(app.AdultA, path);
        var result = await app.GetAsync<MonthResult>(app.AdultA, path);

        Assert.Contains("SharedGym", raw); // positive control: the other adult's shared subscription is there
        Assert.DoesNotContain("SecretTherapy", raw);
        Assert.DoesNotContain(secret.Value.ToString(), raw);
        Assert.Equal(20m, decimal.Parse(result.Total!.Amount, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task The_month_straddling_the_horizon_keeps_its_charges_up_to_it_with_a_finding()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await MonthlyAsync(app, app.AdultA, "Netflix", "10.00", Visibility.Shared);

        var result = await app.GetAsync<MonthResult>(app.AdultA, "/api/v1/subscriptions/charges?month=2035-10");

        Assert.Equal("2035-10-01", Assert.Single(result.Charges).Date);
        Assert.Contains(result.Findings, f => f.Code == "subscriptions.horizon_reached" && f.Message.Contains("stops within this month"));
    }

    [Fact]
    public async Task A_past_month_keeps_the_charges_of_a_subscription_that_ended_before_today()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("Ended", app.AdultA, Visibility.Shared);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn = "2025-01-05", endsOn = "2025-06-01",
            price = new { amount = "9.00", currency = "EUR" },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();

        var result = await app.GetAsync<MonthResult>(app.AdultA, "/api/v1/subscriptions/charges?month=2025-03");

        Assert.Equal("2025-03-05", Assert.Single(result.Charges).Date);
        Assert.Equal(9m, decimal.Parse(result.Total!.Amount, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("2035-11")]
    [InlineData("9999-12")]
    public async Task A_month_beyond_the_horizon_is_empty_with_a_finding(string month)
    {
        await using var app = await NidApp.StartAsync(fixture);
        await MonthlyAsync(app, app.AdultA, "Netflix", "10.00", Visibility.Shared);

        var result = await app.GetAsync<MonthResult>(app.AdultA, "/api/v1/subscriptions/charges?month=" + month);

        Assert.Empty(result.Charges);
        Assert.Equal(0m, decimal.Parse(result.Total!.Amount, CultureInfo.InvariantCulture));
        Assert.Contains(result.Findings, f => f.Code == "subscriptions.horizon_reached");
    }
}
