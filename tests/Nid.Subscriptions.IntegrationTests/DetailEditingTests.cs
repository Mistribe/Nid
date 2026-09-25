using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests;

/// <summary>1E2a design §2.4.</summary>
[Collection("postgres")]
public class DetailEditingTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_detail_carries_owners_phases_and_whether_the_reader_may_write()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m); // shared, owned by A alone

        var forA = await app.GetAsync<JsonElement>(app.AdultA, $"/api/v1/subscriptions/{id.Value}");
        var forB = await app.GetAsync<JsonElement>(app.AdultB, $"/api/v1/subscriptions/{id.Value}");

        Assert.True(forA.GetProperty("canWrite").GetBoolean());
        Assert.False(forB.GetProperty("canWrite").GetBoolean());
        Assert.Equal("shared", forA.GetProperty("visibility").GetString());
        var owner = Assert.Single(forA.GetProperty("owners").EnumerateArray());
        Assert.Equal((app.AdultA.Value, 10_000), (owner.GetProperty("personId").GetGuid(), owner.GetProperty("shareBp").GetInt32()));
        var phase = Assert.Single(forA.GetProperty("phases").EnumerateArray());
        Assert.Equal("2025-01-01", phase.GetProperty("startsOn").GetString());
        Assert.Equal("month", phase.GetProperty("covers").GetProperty("unit").GetString());
    }

    [Fact]
    public async Task A_grant_to_an_invisible_subscription_is_left_out_of_the_detail()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewSubscriptionAsync("shared", app.AdultA, Visibility.Shared);
        var secret = await app.NewSubscriptionAsync("secret", app.AdultA, Visibility.Private);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{shared.Value}/purchases", new
        {
            date = "2025-01-01", label = "Bundle", kind = "bundle", allocatedAmount = "10",
            price = new { amount = "30", currency = "EUR" }, payer = new { type = "household" },
            grants = new[]
            {
                new { subscriptionId = shared.Value, duration = new { count = 1, unit = "month" }, source = "included", startsOn = (string?)null },
                new { subscriptionId = secret.Value, duration = new { count = 1, unit = "month" }, source = "included", startsOn = (string?)null },
            },
        })).EnsureSuccessStatusCode();

        var forB = await app.GetAsync<JsonElement>(app.AdultB, $"/api/v1/subscriptions/{shared.Value}");
        var forA = await app.GetAsync<JsonElement>(app.AdultA, $"/api/v1/subscriptions/{shared.Value}");

        var grants = Assert.Single(forB.GetProperty("purchases").EnumerateArray()).GetProperty("grants").EnumerateArray().ToList();
        Assert.Equal([shared.Value], grants.Select(g => g.GetProperty("subscriptionId").GetGuid()));
        Assert.Equal(2, Assert.Single(forA.GetProperty("purchases").EnumerateArray()).GetProperty("grants").GetArrayLength());
        Assert.DoesNotContain(secret.Value.ToString(), forB.GetRawText());
    }
}
