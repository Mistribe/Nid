using System.Net;
using System.Net.Http.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests;

public class WriteEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task An_adult_creates_a_subscription_and_reads_it_back()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var id = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);

        var detail = await app.GetAsync<SubscriptionApi.DetailResponse>(app.AdultA, $"/api/v1/subscriptions/{id.Value}");
        Assert.Equal("Netflix", detail.DisplayName);
    }

    [Fact]
    public async Task A_price_change_closes_one_phase_and_opens_another()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("Spotify", app.AdultA, Visibility.Shared);

        var opened = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn = "2024-06-28",
            price = new { amount = "15.17", currency = "EUR" },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        });
        opened.EnsureSuccessStatusCode();
        var phaseId = (await opened.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();

        (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"/api/v1/subscriptions/{id.Value}/phases/{phaseId}",
            new { endsOn = "2025-06-28" })).EnsureSuccessStatusCode();
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn = "2025-06-28",
            price = new { amount = "17.20", currency = "EUR" },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();

        var detail = await app.GetAsync<SubscriptionApi.DetailResponse>(
            app.AdultA, $"/api/v1/subscriptions/{id.Value}?horizon=2025-12-01");
        Assert.Contains(detail.Charges, c => c.Price.Amount == "15.17");
        Assert.Contains(detail.Charges, c => c.Price.Amount == "17.20");
    }

    [Fact]
    public async Task Overlapping_phases_are_refused_with_the_engines_code()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("Spotify", app.AdultA, Visibility.Shared);

        foreach (var starts in new[] { "2025-01-01", "2025-03-01" })
            (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
            {
                startsOn = starts,
                price = new { amount = "10", currency = "EUR" },
                every = new { count = 1, unit = "month" },
                payer = new { type = "household" },
            })).EnsureSuccessStatusCode();

        // The engine refuses the input when it is read, and the failure carries its stable code.
        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/subscriptions/{id.Value}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        Assert.Contains("subscriptions.phase_overlap", body);
    }

    [Fact]
    public async Task The_other_adult_cannot_write_a_private_subscription_and_is_told_it_does_not_exist()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var privateOfA = await app.NewSubscriptionAsync("Therapy", app.AdultA, Visibility.Private);

        var response = await app.RawAsync(app.AdultB, HttpMethod.Patch, $"/api/v1/subscriptions/{privateOfA.Value}",
            new { autoRenew = false });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_other_adult_can_read_but_not_write_a_shared_subscription_owned_by_one_of_them()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var sharedOfA = await app.NewSubscriptionAsync("Spotify", app.AdultA, Visibility.Shared);

        var read = await app.RawAsync(app.AdultB, HttpMethod.Get, $"/api/v1/subscriptions/{sharedOfA.Value}");
        var write = await app.RawAsync(app.AdultB, HttpMethod.Patch, $"/api/v1/subscriptions/{sharedOfA.Value}",
            new { autoRenew = false });

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task Cancelling_stops_future_charges_without_erasing_history()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);

        (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"/api/v1/subscriptions/{id.Value}",
            new { cancelledAt = "2025-04-01" })).EnsureSuccessStatusCode();

        var detail = await app.GetAsync<SubscriptionApi.DetailResponse>(
            app.AdultA, $"/api/v1/subscriptions/{id.Value}?horizon=2025-12-01");
        Assert.NotEmpty(detail.Charges);
        Assert.All(detail.Charges, c => Assert.True(string.CompareOrdinal(c.Date, "2025-04-01") < 0));
    }

    [Fact]
    public async Task A_deleted_purchase_disappears_from_the_timeline()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("Spotify", app.AdultA, Visibility.Shared);
        var created = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/purchases", new
        {
            date = "2025-01-01", label = "gift", kind = "gift",
            price = new { amount = "0", currency = "EUR" },
            payer = new { type = "external" },
            grants = new[] { new { subscriptionId = id.Value, duration = new { count = 3, unit = "month" }, source = "gift" } },
        });
        created.EnsureSuccessStatusCode();
        var purchaseId = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();

        (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"/api/v1/subscriptions/{id.Value}/purchases/{purchaseId}"))
            .EnsureSuccessStatusCode();

        var detail = await app.GetAsync<SubscriptionApi.DetailResponse>(app.AdultA, $"/api/v1/subscriptions/{id.Value}");
        Assert.Empty(detail.Coverage);
    }

    [Fact]
    public async Task Deleting_a_purchase_through_a_subscription_it_does_not_grant_to_is_not_found_and_leaves_it_intact()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var first = await app.NewSubscriptionAsync("Spotify", app.AdultA, Visibility.Shared);
        var second = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);

        var created = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{first.Value}/purchases", new
        {
            date = "2025-01-01", label = "gift", kind = "gift",
            price = new { amount = "0", currency = "EUR" },
            payer = new { type = "external" },
            grants = new[] { new { subscriptionId = first.Value, duration = new { count = 3, unit = "month" }, source = "gift" } },
        });
        created.EnsureSuccessStatusCode();
        var purchaseId = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();

        // The actor has write access to `second` (they own it), but the purchase grants only to `first`:
        // deleting it through `second`'s route must fail as if the purchase did not exist there.
        var response = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"/api/v1/subscriptions/{second.Value}/purchases/{purchaseId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var detail = await app.GetAsync<SubscriptionApi.DetailResponse>(app.AdultA, $"/api/v1/subscriptions/{first.Value}");
        Assert.NotEmpty(detail.Coverage);
    }
}
