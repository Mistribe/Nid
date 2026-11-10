using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using static Nid.TestSupport.PropertyApi;

namespace Nid.Subscriptions.IntegrationTests;

/// <summary>#23: a subscription created by mistake is deleted after a preview, with its phases, users, insurance and grants; a purchase shared with another subscription stays; the property stays.</summary>
public class SubscriptionDeleteTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static string Url(ItemId id) => $"/api/v1/subscriptions/{id.Value}";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<JsonElement> PreviewAsync(NidApp app, PersonId actor, ItemId id)
    {
        var response = await app.RawAsync(actor, HttpMethod.Get, $"{Url(id)}/deletion");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await JsonAsync(response);
    }

    private static int Removed(JsonElement preview, string key)
        => preview.GetProperty("removes").EnumerateArray().Where(r => r.GetProperty("key").GetString() == key).Select(r => r.GetProperty("count").GetInt32()).SingleOrDefault(); // a zero count is not listed

    private static async Task<uint> VersionAsync(NidApp app, ItemId id) => (await PreviewAsync(app, app.AdultA, id)).GetProperty("version").GetUInt32();

    private static Task<HttpResponseMessage> DeleteAsync(NidApp app, ItemId id, uint version)
        => app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Url(id)}?version={version}");

    private static async Task<HttpStatusCode> StatusAsync(NidApp app, ItemId id) => (await app.RawAsync(app.AdultA, HttpMethod.Get, Url(id))).StatusCode;

    private static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    /// <summary>A subscription with two phases, an insurance of two guarantees on a property, and a gift purchase granting to it.</summary>
    private static async Task<(ItemId Subscription, Guid Property)> FilledAsync(NidApp app)
    {
        var id = await app.NewSpotifyGoldenCaseAsync(app.AdultA);
        var property = await NewPropertyAsync(app, Home(app));
        var put = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Url(id)}/insurance", new
        {
            kind = "home", propertyId = property,
            guarantees = new[] { new { label = "Incendie", ceiling = "100 000", deductible = "150" }, new { label = "Dégâts des eaux", ceiling = "50 000", deductible = "150" } },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        return (id, property);
    }

    [Fact]
    public async Task Preview_counts_phases_grants_insurance_and_users()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, _) = await FilledAsync(app);

        var preview = await PreviewAsync(app, app.AdultA, id);

        Assert.Equal("Spotify Premium Duo", preview.GetProperty("name").GetString());
        Assert.False(preview.GetProperty("imported").GetBoolean());
        Assert.Equal(2, Removed(preview, "billing_phases"));
        Assert.Equal(1, Removed(preview, "coverage_grants"));
        Assert.Equal(1, Removed(preview, "purchases"));
        Assert.Equal(1, Removed(preview, "subscription_insurance"));
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        Assert.Equal(await subs.Set<Nid.Subscriptions.Infrastructure.Persistence.SubscriptionUserRow>().CountAsync(u => u.ItemId == id.Value, TestContext.Current.CancellationToken),
            Removed(preview, "subscription_users"));
        Assert.Empty(preview.GetProperty("blockers").EnumerateArray());
    }

    [Fact]
    public async Task Delete_removes_the_subscription_with_its_rows_and_removes_the_purchase_it_alone_covers_and_keeps_the_property()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, property) = await FilledAsync(app);

        var deleted = await DeleteAsync(app, id, await VersionAsync(app, id));

        Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(app, id));
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        Assert.Empty(subs.Subscriptions.Where(s => s.ItemId == id.Value));
        Assert.Empty(subs.Phases.Where(p => p.ItemId == id.Value));
        Assert.Empty(subs.Insurances.Where(i => i.ItemId == id.Value));
        Assert.Empty(subs.InsuranceGuarantees.Where(g => g.ItemId == id.Value));
        Assert.Empty(subs.Grants.Where(g => g.SubscriptionItemId == id.Value));
        Assert.Empty(subs.Set<Nid.Subscriptions.Infrastructure.Persistence.SubscriptionUserRow>().Where(u => u.ItemId == id.Value));
        Assert.Empty(subs.Purchases);
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.Empty(core.Flows.Where(f => f.ItemId == id.Value));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Properties}/{property}")).StatusCode);
    }

    [Fact]
    public async Task A_bundle_purchase_shared_with_another_subscription_stays_with_its_other_grant()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        var other = await app.NewSubscriptionAsync("Disney", app.AdultA, Visibility.Shared);
        var bought = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(mine)}/purchases", new
        {
            date = "2025-10-10", label = "Pack", kind = "bundle", price = new { amount = "10", currency = "EUR" }, allocatedAmount = "0", payer = new { type = "household" },
            grants = new object[]
            {
                new { duration = new { count = 1, unit = "month" }, source = "included" },
                new { subscriptionId = other.Value, duration = new { count = 1, unit = "month" }, source = "included" },
            },
        });
        Assert.True(bought.IsSuccessStatusCode, await bought.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var preview = await PreviewAsync(app, app.AdultA, mine);
        Assert.Equal(0, Removed(preview, "purchases"));
        Assert.Equal(1, Removed(preview, "coverage_grants"));
        var deleted = await DeleteAsync(app, mine, preview.GetProperty("version").GetUInt32());

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        Assert.Single(subs.Purchases);
        var grant = Assert.Single(subs.Grants);
        Assert.Equal(other.Value, grant.SubscriptionItemId);
    }

    [Fact]
    public async Task Stale_version_answers_409()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        var stale = await VersionAsync(app, id);
        var renamed = await app.RawAsync(app.AdultA, HttpMethod.Patch, Url(id), new { displayName = "Netflix Duo" });
        Assert.True(renamed.IsSuccessStatusCode, await renamed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        await AssertRefusedAsync(await DeleteAsync(app, id, stale), HttpStatusCode.Conflict, ItemCodes.Changed);

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(app, id));
    }

    [Fact]
    public async Task Missing_version_answers_422()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Delete, Url(id));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("version", (await JsonAsync(response)).GetProperty("errors")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Reader_gets_403_and_stranger_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        var secret = await app.NewSubscriptionAsync("Cabanon TV", app.AdultA, Visibility.Private);

        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Url(shared)}/deletion")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{Url(shared)}?version=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Url(secret)}/deletion")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{Url(secret)}?version=1")).StatusCode);
    }

    [Fact]
    public async Task Write_direct_token_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        var version = await VersionAsync(app, id);
        var token = await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Direct);

        await AssertRefusedAsync(await app.BearerAsync(token.Secret, HttpMethod.Delete, $"{Url(id)}?version={version}"), HttpStatusCode.Forbidden, BrowserOnlyCodes.Required);

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(app, id));
    }
}
