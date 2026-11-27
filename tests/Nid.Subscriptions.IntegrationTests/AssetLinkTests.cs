using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using NodaTime;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests;

/// <summary>#648: a subscription (an insurance, a service contract) is linked to an asset of « Autres biens ».</summary>
public class AssetLinkTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static string SubscriptionUrl(ItemId id) => $"/api/v1/subscriptions/{id.Value}";

    private static async Task<JsonElement> Body(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<ItemId> NewAssetAsync(NidApp app, PersonId owner, string visibility = "shared", string name = "Voiture")
    {
        var response = await app.RawAsync(owner, HttpMethod.Post, "/api/v1/assets", new
        {
            name, kind = "vehicle", purchasePrice = "10000", purchasedOn = "2025-01-10", visibility,
            owners = new[] { new { personId = owner.Value, shareBp = 10_000 } },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return new ItemId((await Body(response)).GetProperty("id").GetGuid());
    }

    private static Task<HttpResponseMessage> LinkAsync(NidApp app, PersonId actor, ItemId subscription, ItemId? asset)
        => app.RawAsync(actor, HttpMethod.Put, $"{SubscriptionUrl(subscription)}/asset", new { assetId = asset?.Value });

    private static async Task<JsonElement> AssetOfAsync(NidApp app, PersonId reader, ItemId subscription)
        => (await app.GetAsync<JsonElement>(reader, SubscriptionUrl(subscription))).GetProperty("asset");

    private static async Task<List<JsonElement>> SubscriptionsOfAsync(NidApp app, PersonId reader, ItemId asset)
        => (await app.GetAsync<JsonElement>(reader, $"/api/v1/assets/{asset.Value}")).GetProperty("subscriptions").EnumerateArray().ToList();

    [Fact]
    public async Task Link_then_unlink_shows_on_both_sides()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var subscription = await app.NewMonthlyAsync(app.AdultA, 10m);
        var asset = await NewAssetAsync(app, app.AdultA);

        Assert.Equal(HttpStatusCode.OK, (await LinkAsync(app, app.AdultA, subscription, asset)).StatusCode);

        var linked = await AssetOfAsync(app, app.AdultA, subscription);
        Assert.Equal(asset.Value, linked.GetProperty("id").GetGuid());
        Assert.Equal("Voiture", linked.GetProperty("name").GetString());
        var listed = Assert.Single(await SubscriptionsOfAsync(app, app.AdultA, asset));
        Assert.Equal(subscription.Value, listed.GetProperty("id").GetGuid());
        Assert.Equal("10", listed.GetProperty("price").GetProperty("amount").GetString());
        Assert.Equal(("month", 1), (listed.GetProperty("every").GetProperty("unit").GetString(), listed.GetProperty("every").GetProperty("count").GetInt32()));
        Assert.NotEqual(JsonValueKind.Null, listed.GetProperty("nextDeadline").ValueKind);

        Assert.Equal(HttpStatusCode.OK, (await LinkAsync(app, app.AdultA, subscription, null)).StatusCode);

        Assert.Equal(JsonValueKind.Null, (await AssetOfAsync(app, app.AdultA, subscription)).ValueKind);
        Assert.Empty(await SubscriptionsOfAsync(app, app.AdultA, asset));
    }

    [Fact]
    public async Task A_linked_insurance_costs_its_charges_in_the_period()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var insurance = await app.NewMonthlyAsync(app.AdultA, 50m);
        var hidden = await app.NewSubscriptionAsync("Privé de A", app.AdultA, Visibility.Private);
        var asset = await NewAssetAsync(app, app.AdultA);
        Assert.Equal(HttpStatusCode.OK, (await LinkAsync(app, app.AdultA, insurance, asset)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LinkAsync(app, app.AdultA, hidden, asset)).StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var port = scope.ServiceProvider.GetRequiredService<ISubscriptionsOfItems>();

        var costs = (await port.CostsAsync(app.AdultB, VisibilityScope.Me, [asset], new LocalDate(2025, 3, 1), new LocalDate(2025, 5, 31),
            TestContext.Current.CancellationToken))[asset];

        var cost = Assert.Single(costs);
        Assert.Equal((insurance, 150m, "EUR"), (cost.Id, cost.Cost!.Value.Amount, cost.Cost!.Value.Currency.Code));
    }

    [Fact]
    public async Task Create_with_an_asset_links_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var asset = await NewAssetAsync(app, app.AdultA);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions", new
        {
            displayName = "Assurance auto", visibility = "shared", autoRenew = true, assetId = asset.Value,
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = new ItemId((await Body(response)).GetProperty("id").GetGuid());
        Assert.Equal(asset.Value, (await AssetOfAsync(app, app.AdultA, id)).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task An_item_that_is_no_asset_or_not_readable_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var subscription = await app.NewMonthlyAsync(app.AdultA, 10m);
        var property = await app.NewPropertyAsync("Maison", Visibility.Shared, (app.AdultA, 10_000));
        var hidden = await NewAssetAsync(app, app.AdultB, "private");

        foreach (var target in new[] { property, hidden, new ItemId(Guid.NewGuid()) })
        {
            var response = await LinkAsync(app, app.AdultA, subscription, target);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var error = Assert.Single((await Body(response)).GetProperty("errors").EnumerateArray());
            Assert.Equal(("assetId", "subscriptions.invalid_asset"), (error.FieldPath(), error.GetProperty("code").GetString()));
        }

        Assert.Equal(JsonValueKind.Null, (await AssetOfAsync(app, app.AdultA, subscription)).ValueKind);
    }

    [Fact]
    public async Task A_private_asset_is_not_named_to_another_member_and_a_private_subscription_is_not_listed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewOwnedSubscriptionAsync("Assurance", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));
        var privateAsset = await NewAssetAsync(app, app.AdultA, "private");
        Assert.Equal(HttpStatusCode.OK, (await LinkAsync(app, app.AdultA, shared, privateAsset)).StatusCode);

        Assert.Equal(JsonValueKind.Null, (await AssetOfAsync(app, app.AdultB, shared)).ValueKind);
        Assert.Single(await SubscriptionsOfAsync(app, app.AdultA, privateAsset));

        var sharedAsset = await NewAssetAsync(app, app.AdultA, "shared", "Moto");
        var secret = await app.NewOwnedSubscriptionAsync("Secret", Visibility.Private, (app.AdultA, 10_000));
        Assert.Equal(HttpStatusCode.OK, (await LinkAsync(app, app.AdultA, secret, sharedAsset)).StatusCode);

        Assert.Single(await SubscriptionsOfAsync(app, app.AdultA, sharedAsset));
        Assert.Empty(await SubscriptionsOfAsync(app, app.AdultB, sharedAsset));
    }

    [Fact]
    public async Task Deleting_the_asset_keeps_the_subscription_and_drops_the_link()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var subscription = await app.NewMonthlyAsync(app.AdultA, 10m);
        var asset = await NewAssetAsync(app, app.AdultA);
        await LinkAsync(app, app.AdultA, subscription, asset);
        var preview = await Body(await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/assets/{asset.Value}/deletion"));
        var unlinked = Assert.Single(preview.GetProperty("removes").EnumerateArray(), r => r.GetProperty("key").GetString() == "linked_subscriptions");
        Assert.Equal(1, unlinked.GetProperty("count").GetInt32());

        var deleted = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"/api/v1/assets/{asset.Value}?version={preview.GetProperty("version").GetUInt32()}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await AssetOfAsync(app, app.AdultA, subscription)).ValueKind);
    }

    [Fact]
    public async Task Deleting_the_subscription_leaves_the_asset_without_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var subscription = await app.NewMonthlyAsync(app.AdultA, 10m);
        var asset = await NewAssetAsync(app, app.AdultA);
        await LinkAsync(app, app.AdultA, subscription, asset);
        var preview = await Body(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{SubscriptionUrl(subscription)}/deletion"));

        var deleted = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{SubscriptionUrl(subscription)}?version={preview.GetProperty("version").GetUInt32()}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(await SubscriptionsOfAsync(app, app.AdultA, asset));
    }

    [Fact]
    public async Task A_member_who_cannot_see_the_linked_asset_can_neither_replace_nor_remove_the_link()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewOwnedSubscriptionAsync("Assurance", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));
        var privateAsset = await NewAssetAsync(app, app.AdultA, "private");
        var other = await NewAssetAsync(app, app.AdultB, "shared", "Moto");
        await LinkAsync(app, app.AdultA, shared, privateAsset);

        var detail = await app.GetAsync<JsonElement>(app.AdultB, SubscriptionUrl(shared));
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("asset").ValueKind);
        Assert.True(detail.GetProperty("assetHidden").GetBoolean());
        foreach (var target in new ItemId?[] { null, other })
        {
            var response = await LinkAsync(app, app.AdultB, shared, target);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var error = Assert.Single((await Body(response)).GetProperty("errors").EnumerateArray());
            Assert.Equal(("assetId", "subscriptions.asset_hidden"), (error.FieldPath(), error.GetProperty("code").GetString()));
        }

        Assert.Equal(privateAsset.Value, (await AssetOfAsync(app, app.AdultA, shared)).GetProperty("id").GetGuid());
        Assert.False((await app.GetAsync<JsonElement>(app.AdultA, SubscriptionUrl(shared))).GetProperty("assetHidden").GetBoolean());
    }

    [Fact]
    public async Task Create_with_a_hidden_asset_or_a_non_asset_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await NewAssetAsync(app, app.AdultB, "private");
        var property = await app.NewPropertyAsync("Maison", Visibility.Shared, (app.AdultA, 10_000));

        foreach (var target in new[] { hidden, property })
        {
            var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions", new
            {
                displayName = "Assurance", visibility = "shared", autoRenew = true, assetId = target.Value,
                owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var error = Assert.Single((await Body(response)).GetProperty("errors").EnumerateArray());
            Assert.Equal(("assetId", "subscriptions.invalid_asset"), (error.FieldPath(), error.GetProperty("code").GetString()));
        }
    }

    [Fact]
    public async Task The_list_filter_asset_none_keeps_the_subscriptions_without_an_asset()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var linked = await app.NewMonthlyAsync(app.AdultA, 10m);
        var free = await app.NewMonthlyAsync(app.AdultA, 12m);
        await LinkAsync(app, app.AdultA, linked, await NewAssetAsync(app, app.AdultA));

        var list = await app.GetAsync<JsonElement>(app.AdultA, "/api/v1/subscriptions?asset=none");

        Assert.Equal([free.Value], list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task A_phase_billing_every_three_months_is_sent_with_its_count_and_unit()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var subscription = await app.NewOwnedSubscriptionAsync("Assurance", Visibility.Shared, (app.AdultA, 10_000));
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{SubscriptionUrl(subscription)}/phases", new
        {
            startsOn = "2025-01-01", price = new { amount = "90", currency = "EUR" }, every = new { count = 3, unit = "month" }, payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        var asset = await NewAssetAsync(app, app.AdultA);
        await LinkAsync(app, app.AdultA, subscription, asset);

        var every = Assert.Single(await SubscriptionsOfAsync(app, app.AdultA, asset)).GetProperty("every");

        Assert.Equal((3, "month"), (every.GetProperty("count").GetInt32(), every.GetProperty("unit").GetString()));
    }
}
