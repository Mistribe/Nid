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
    public async Task An_owner_renames_a_subscription_and_a_blank_or_long_name_is_refused_on_its_field()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        var path = $"/api/v1/subscriptions/{id.Value}";

        (await app.RawAsync(app.AdultA, HttpMethod.Patch, path, new { displayName = "Netflix Premium" })).EnsureSuccessStatusCode();
        Assert.Equal("Netflix Premium", (await app.GetAsync<SubscriptionApi.DetailResponse>(app.AdultA, path)).DisplayName);

        foreach (var name in new[] { "  ", new string('x', 101) })
        {
            var refused = await app.RawAsync(app.AdultA, HttpMethod.Patch, path, new { displayName = name, autoRenew = false });
            var body = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.UnprocessableContent, refused.StatusCode);
            Assert.Contains("\"pointer\":\"#/displayName\"", body);
        }
        Assert.Equal("Netflix Premium", (await app.GetAsync<SubscriptionApi.DetailResponse>(app.AdultA, path)).DisplayName);
    }

    [Fact]
    public async Task A_subscription_is_not_created_with_a_blank_or_long_name()
    {
        await using var app = await NidApp.StartAsync(fixture);

        foreach (var name in new[] { "  ", new string('x', 101) })
        {
            var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions", new
            {
                displayName = name, visibility = "shared", owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } }, autoRenew = true,
            });
            var body = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.UnprocessableContent, refused.StatusCode);
            Assert.Contains("\"pointer\":\"#/displayName\"", body);
        }
        Assert.Equal(0, (await app.GetAsync<System.Text.Json.JsonElement>(app.AdultA, "/api/v1/subscriptions")).GetProperty("items").GetArrayLength());
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
        var phaseId = (await opened.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();

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

        var responses = new List<HttpResponseMessage>();
        foreach (var starts in new[] { "2025-01-01", "2025-03-01" })
            responses.Add(await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", OpenPhase(starts)));

        // #628: the second phase is refused when written, with the engine's stable code, and the subscription still reads.
        var body = await responses[1].Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.UnprocessableContent], responses.Select(r => r.StatusCode));
        Assert.Contains("subscriptions.phase_overlap", body);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/subscriptions/{id.Value}")).StatusCode);
    }

    /// <summary>#628: a later end that runs into the next phase is refused; the subscription still reads.</summary>
    [Fact]
    public async Task Closing_a_phase_over_the_next_one_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("Spotify", app.AdultA, Visibility.Shared);
        var first = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", OpenPhase("2025-01-01", "2025-03-01"));
        var phaseId = (await first.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", OpenPhase("2025-03-01"))).EnsureSuccessStatusCode();

        var response = await app.RawAsync(app.AdultA, HttpMethod.Patch, $"/api/v1/subscriptions/{id.Value}/phases/{phaseId}", new { endsOn = "2025-06-01" });

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        var error = Assert.Single((await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("errors").EnumerateArray());
        Assert.Equal(("endsOn", "subscriptions.phase_overlap"), (error.FieldPath(), error.GetProperty("code").GetString()));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/subscriptions/{id.Value}")).StatusCode);
    }

    /// <summary>#628: a purchase is read and deleted through its grant to the subscription it is recorded on.</summary>
    [Fact]
    public async Task A_purchase_that_grants_nothing_to_its_subscription_is_refused_at_its_grants()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("Spotify", app.AdultA, Visibility.Shared);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/purchases", new
        {
            date = "2025-01-01", label = "refund", kind = "refund",
            price = new { amount = "-5", currency = "EUR" },
            payer = new { type = "household" },
            grants = Array.Empty<object>(),
        });

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        var error = Assert.Single((await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("errors").EnumerateArray());
        Assert.Equal(("grants", "subscriptions.grant_missing"), (error.FieldPath(), error.GetProperty("code").GetString()));
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        Assert.Empty(subs.Purchases);
    }

    private static object OpenPhase(string startsOn, string? endsOn = null) => new
    {
        startsOn, endsOn,
        price = new { amount = "10", currency = "EUR" },
        every = new { count = 1, unit = "month" },
        payer = new { type = "household" },
    };

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
        var purchaseId = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();

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
        var purchaseId = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();

        // The actor has write access to `second` (they own it), but the purchase grants only to `first`:
        // deleting it through `second`'s route must fail as if the purchase did not exist there.
        var response = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"/api/v1/subscriptions/{second.Value}/purchases/{purchaseId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var detail = await app.GetAsync<SubscriptionApi.DetailResponse>(app.AdultA, $"/api/v1/subscriptions/{first.Value}");
        Assert.NotEmpty(detail.Coverage);
    }
}
