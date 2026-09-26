using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests;

/// <summary>1E2b1 design §3.2: the purchase route reads its body by field and answers every refusal with a path.</summary>
[Collection("postgres")]
public class PurchaseRouteTests(PostgresFixture fixture)
{
    private static object Purchase(
        string kind = "prepaid_term", string amount = "59.88", string? allocatedAmount = null, string date = "2025-01-01",
        object[]? grants = null) => new
    {
        date, label = "Prépayé", price = new { amount, currency = "EUR" }, payer = new { type = "household" }, kind,
        grants = grants ?? [new { duration = new { count = 12, unit = "month" }, source = "paid" }],
        allocatedAmount, note = (string?)null,
    };

    private static async Task<List<(string Path, string Code)>> ErrorsOf(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").EnumerateArray()
            .Select(e => (e.GetProperty("path").GetString()!, e.GetProperty("code").GetString()!)).ToList();

    private static async Task<int> PurchaseCount(NidApp app)
    {
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        return await subs.Purchases.CountAsync();
    }

    [Fact]
    public async Task A_grant_without_a_subscription_covers_the_routes_subscription()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("vpn", app.AdultA, Visibility.Shared);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/purchases", Purchase());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await app.GetAsync<ReadEndpointTests.DetailResponse>(
            app.AdultA, $"/api/v1/subscriptions/{id.Value}?from=2025-01-01&to=2026-01-01");
        var paid = Assert.Single(detail.Coverage);
        Assert.Equal(("2025-01-01", "2026-01-01", "paid"), (paid.Start, paid.EndBefore, paid.Source));
    }

    [Fact]
    public async Task Shape_errors_come_back_by_path_all_at_once()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("vpn", app.AdultA, Visibility.Shared);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/purchases",
            Purchase(kind: "coupon", amount: "9,99", date: "2025-02-30",
                grants: [new { duration = new { count = 1, unit = "fortnight" }, source = "free" }]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            ["date", "grants[0].duration.unit", "grants[0].source", "kind", "price.amount"],
            (await ErrorsOf(response)).Select(e => e.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_bundle_allocating_more_than_its_price_is_refused_at_allocatedAmount_and_nothing_is_written()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("tv", app.AdultA, Visibility.Shared);
        var before = await PurchaseCount(app);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/purchases",
            Purchase(kind: "bundle", amount: "88.99", allocatedAmount: "90",
                grants: [new { duration = new { count = 3, unit = "month" }, source = "included" }]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal([("allocatedAmount", "subscriptions.allocated_range")], await ErrorsOf(response));
        Assert.Equal(before, await PurchaseCount(app));
    }

    [Fact]
    public async Task An_allocated_amount_on_a_gift_is_refused_at_allocatedAmount()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("music", app.AdultA, Visibility.Shared);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/purchases",
            Purchase(kind: "gift", amount: "0", allocatedAmount: "0",
                grants: [new { duration = new { count = 3, unit = "month" }, source = "gift" }]));

        Assert.Equal([("allocatedAmount", "subscriptions.allocated_not_bundle")], await ErrorsOf(response));
    }

    [Fact]
    public async Task A_target_the_actor_cannot_write_answers_exactly_like_a_missing_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await app.NewSubscriptionAsync("mine", app.AdultA, Visibility.Shared);
        var secret = await app.NewSubscriptionAsync("secret", app.AdultB, Visibility.Private);
        object Naming(Guid other) => Purchase(kind: "bundle", amount: "10", allocatedAmount: "0", grants:
        [
            new { duration = new { count = 1, unit = "month" }, source = "included" },
            new { subscriptionId = other, duration = new { count = 1, unit = "month" }, source = "included" },
        ]);

        var hidden = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{mine.Value}/purchases", Naming(secret.Value));
        var missing = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{mine.Value}/purchases", Naming(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, hidden.StatusCode);
        Assert.Equal([("grants[1].subscriptionId", "subscriptions.not_found")], await ErrorsOf(hidden));
        var bodyOf = async (HttpResponseMessage r) => JsonSerializer.Serialize(
            (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors"));
        // Read once each: ErrorsOf above consumed `hidden`, so compare from fresh requests.
        var again = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{mine.Value}/purchases", Naming(secret.Value));
        Assert.Equal(await bodyOf(missing), await bodyOf(again));
    }
}
