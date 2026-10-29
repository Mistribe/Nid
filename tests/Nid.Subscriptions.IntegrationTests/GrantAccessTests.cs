using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests;

/// <summary>
/// A purchase's grants are the only place in the module where a single write touches a second item: the
/// route names one subscription, the body may name any other. <c>authorization.md</c> §5 says writing is
/// <c>Writers</c> of the item written, so every grant — the ones a create carries and the ones a delete
/// cascades into — is checked against the actor, and §6 says a refusal never discloses existence.
/// </summary>
public class GrantAccessTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_purchase_cannot_grant_to_the_other_adults_private_subscription()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await app.NewSubscriptionAsync("mine", app.AdultA, Visibility.Shared);
        var secret = await app.NewSubscriptionAsync("secret", app.AdultB, Visibility.Private);

        // A may write `mine`, so the route passes the item-access behavior. The grant names `secret`, which
        // A may not even read: the purchase must be refused, and as 'not found' — never as 'forbidden'.
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{mine.Value}/purchases",
            GiftPurchase(secret, months: 36));

        // 1E2b1 design §3.2: refused like a missing subscription, at the grant that names it.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal("subscriptions.not_found", errors[0].GetProperty("code").GetString());

        // And nothing was written on the way out: B's subscription still renews.
        var detail = await app.GetAsync<SubscriptionApi.DetailResponse>(app.AdultB, $"/api/v1/subscriptions/{secret.Value}");
        Assert.Empty(detail.Coverage);
    }

    [Fact]
    public async Task A_purchase_cannot_grant_to_a_shared_subscription_the_actor_may_only_read()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await app.NewSubscriptionAsync("mine", app.AdultA, Visibility.Shared);
        var theirs = await app.NewSubscriptionAsync("theirs", app.AdultB, Visibility.Shared);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{mine.Value}/purchases",
            GiftPurchase(theirs, months: 36));

        // 1E2b1 design §3.2: refused like a missing subscription, at the grant that names it.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal("subscriptions.not_found", errors[0].GetProperty("code").GetString());

        var detail = await app.GetAsync<SubscriptionApi.DetailResponse>(app.AdultB, $"/api/v1/subscriptions/{theirs.Value}");
        Assert.Empty(detail.Coverage);
    }

    [Fact]
    public async Task A_purchase_granting_only_to_items_the_actor_writes_is_still_accepted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var first = await app.NewSubscriptionAsync("first", app.AdultA, Visibility.Shared);
        var second = await app.NewSubscriptionAsync("second", app.AdultA, Visibility.Private);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{first.Value}/purchases",
            BundlePurchase(first, second));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        foreach (var item in new[] { first, second })
            Assert.NotEmpty((await app.GetAsync<SubscriptionApi.DetailResponse>(
                app.AdultA, $"/api/v1/subscriptions/{item.Value}")).Coverage);
    }

    [Fact]
    public async Task Deleting_a_purchase_cannot_destroy_coverage_on_a_subscription_the_actor_may_not_write()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var joint = await app.NewOwnedSubscriptionAsync(
            "joint", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));
        var secret = await app.NewSubscriptionAsync("secret", app.AdultB, Visibility.Private);

        // B buys one bundle covering both — the documented core case of subscription-data-model.md.
        var created = await app.RawAsync(app.AdultB, HttpMethod.Post, $"/api/v1/subscriptions/{joint.Value}/purchases",
            BundlePurchase(joint, secret));
        created.EnsureSuccessStatusCode();
        var purchaseId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // A is a legitimate writer of `joint` and reads the purchase id from its detail. Deleting it would
        // cascade into `secret`, which A may not write, so the delete is refused — and undisclosed.
        var response = await app.RawAsync(app.AdultA, HttpMethod.Delete,
            $"/api/v1/subscriptions/{joint.Value}/purchases/{purchaseId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var secretDetail = await app.GetAsync<SubscriptionApi.DetailResponse>(
            app.AdultB, $"/api/v1/subscriptions/{secret.Value}");
        Assert.NotEmpty(secretDetail.Coverage);

        // Refused whole: the joint side is untouched too.
        var jointDetail = await app.GetAsync<SubscriptionApi.DetailResponse>(
            app.AdultA, $"/api/v1/subscriptions/{joint.Value}");
        Assert.NotEmpty(jointDetail.Coverage);
    }

    [Fact]
    public async Task The_owner_of_both_sides_still_deletes_the_shared_purchase()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var joint = await app.NewOwnedSubscriptionAsync(
            "joint", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));
        var secret = await app.NewSubscriptionAsync("secret", app.AdultB, Visibility.Private);

        var created = await app.RawAsync(app.AdultB, HttpMethod.Post, $"/api/v1/subscriptions/{joint.Value}/purchases",
            BundlePurchase(joint, secret));
        created.EnsureSuccessStatusCode();
        var purchaseId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        (await app.RawAsync(app.AdultB, HttpMethod.Delete, $"/api/v1/subscriptions/{joint.Value}/purchases/{purchaseId}"))
            .EnsureSuccessStatusCode();

        Assert.Empty((await app.GetAsync<SubscriptionApi.DetailResponse>(
            app.AdultB, $"/api/v1/subscriptions/{secret.Value}")).Coverage);
    }

    private static object GiftPurchase(ItemId grantTo, int months) => new
    {
        date = "2025-01-01", label = "gift", kind = "gift",
        price = new { amount = "0", currency = "EUR" },
        payer = new { type = "external" },
        grants = new[]
        {
            new { subscriptionId = grantTo.Value, duration = new { count = months, unit = "month" }, source = "gift" },
        },
    };

    private static object BundlePurchase(ItemId first, ItemId second) => new
    {
        date = "2025-01-01", label = "two services", kind = "prepaid_term",
        price = new { amount = "20", currency = "EUR" },
        payer = new { type = "household" },
        grants = new[]
        {
            new { subscriptionId = first.Value, duration = new { count = 12, unit = "month" }, source = "paid" },
            new { subscriptionId = second.Value, duration = new { count = 12, unit = "month" }, source = "paid" },
        },
    };
}
