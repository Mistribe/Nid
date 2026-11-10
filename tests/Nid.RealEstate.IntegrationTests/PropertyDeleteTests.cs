using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>#23: a property created by mistake is deleted after a preview, with its tenancies, costs and facts; a loan or an insurance blocks.</summary>
public class PropertyDeleteTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<JsonElement> PreviewAsync(NidApp app, PersonId actor, Guid property)
    {
        var response = await app.RawAsync(actor, HttpMethod.Get, $"{Properties}/{property}/deletion");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await JsonAsync(response);
    }

    private static int Removed(JsonElement preview, string key)
        => preview.GetProperty("removes").EnumerateArray().Single(r => r.GetProperty("key").GetString() == key).GetProperty("count").GetInt32();

    private static JsonElement Blocker(JsonElement preview, string key)
        => preview.GetProperty("blockers").EnumerateArray().Single(b => b.GetProperty("key").GetString() == key);

    private static async Task<uint> DeletionVersionAsync(NidApp app, Guid property) => (await PreviewAsync(app, app.AdultA, property)).GetProperty("version").GetUInt32();

    private static Task<HttpResponseMessage> DeleteAsync(NidApp app, Guid property, uint version)
        => app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Properties}/{property}?version={version}");

    private static async Task<HttpStatusCode> StatusAsync(NidApp app, Guid property)
        => (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Properties}/{property}")).StatusCode;

    private static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    private static async Task PostAsync(NidApp app, string url, object body)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, url, body);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>A let property: a tenancy revised once (its successor points back at it), a recurring cost, a valuation and a rent flow.</summary>
    private static async Task<Guid> FilledAsync(NidApp app)
    {
        var id = await NewPropertyAsync(app, Home(app, "Studio"));
        var url = $"{Properties}/{id}";
        var tenancy = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/tenancies",
            new { label = "T1", startsOn = "2024-01-01", rentMonthly = "600", chargesMonthly = "40" }));
        await PostAsync(app, $"{url}/tenancies/{tenancy}/revision", new { effectiveOn = "2025-06-01", newRent = "650" });
        await PostAsync(app, $"{url}/recurring-costs",
            new { category = "property_tax", amount = "800", every = new { count = 1, unit = "year" }, startsOn = "2021-10-15" });
        await PostAsync(app, $"{url}/valuations", new { asOf = "2025-06-01", amount = "260000" });
        await PostAsync(app, $"{url}/flows", new { occurredOn = "2025-06-05", kind = "income", amount = "650", category = "rent" });
        return id;
    }

    [Fact]
    public async Task Preview_counts_tenancies_costs_and_facts()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var property = await FilledAsync(app);

        var preview = await PreviewAsync(app, app.AdultA, property);

        Assert.Equal("Studio", preview.GetProperty("name").GetString());
        Assert.False(preview.GetProperty("imported").GetBoolean());
        Assert.Equal(2, Removed(preview, "tenancies"));
        Assert.Equal(1, Removed(preview, "recurring_costs"));
        Assert.Equal(1, Removed(preview, "valuations"));
        Assert.Equal(1, Removed(preview, "flows"));
        Assert.Empty(preview.GetProperty("blockers").EnumerateArray());
    }

    [Fact]
    public async Task Delete_removes_the_property_its_tenancies_costs_and_facts()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var property = await FilledAsync(app);

        var deleted = await DeleteAsync(app, property, await DeletionVersionAsync(app, property));

        Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(app, property));
        await using var realestate = PostgresFixture.NewRealEstateContext(app.ConnectionString);
        Assert.Empty(realestate.Properties.Where(p => p.ItemId == property));
        Assert.Empty(realestate.Tenancies.Where(t => t.PropertyItemId == property));
        Assert.Empty(realestate.RecurringCosts.Where(c => c.PropertyItemId == property));
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.Empty(core.Flows.Where(f => f.ItemId == property));
        Assert.Empty(core.Valuations.Where(v => v.ItemId == property));
    }

    [Fact]
    public async Task A_chain_of_three_tenancies_is_removed_without_a_key_error()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var property = await NewPropertyAsync(app, Home(app, "Studio"));
        var url = $"{Properties}/{property}";
        var current = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/tenancies",
            new { label = "T1", startsOn = "2022-01-01", rentMonthly = "600", chargesMonthly = "40" }));
        foreach (var on in new[] { "2023-01-01", "2024-01-01" })
            current = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/tenancies/{current}/revision", new { effectiveOn = on, newRent = "650" }));

        var deleted = await DeleteAsync(app, property, await DeletionVersionAsync(app, property));

        Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        await using var realestate = PostgresFixture.NewRealEstateContext(app.ConnectionString);
        Assert.Empty(realestate.Tenancies.Where(t => t.PropertyItemId == property));
    }

    [Fact]
    public async Task A_loan_financing_it_blocks_and_is_named()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var property = await NewPropertyAsync(app, Home(app));
        var body = Short(app, "Prêt travaux");
        body["propertyId"] = property;
        var loan = await LoanApi.NewAsync(app, body);

        var blocker = Blocker(await PreviewAsync(app, app.AdultA, property), "loan");
        await AssertRefusedAsync(await DeleteAsync(app, property, await DeletionVersionAsync(app, property)), HttpStatusCode.Conflict, ItemCodes.InUse);

        Assert.Equal(1, blocker.GetProperty("count").GetInt32());
        var named = Assert.Single(blocker.GetProperty("items").EnumerateArray());
        Assert.Equal((loan, "Prêt travaux"), (named.GetProperty("id").GetGuid(), named.GetProperty("name").GetString()));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(app, property));
    }

    [Fact]
    public async Task A_subscription_insurance_blocks_and_names_the_subscription()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var property = await NewPropertyAsync(app, Home(app));
        var subscription = await app.NewSubscriptionAsync("Assurance habitation", app.AdultA, Visibility.Shared);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"/api/v1/subscriptions/{subscription.Value}/insurance",
            new { kind = "home", propertyId = property, guarantees = Array.Empty<object>() })).StatusCode);

        var blocker = Blocker(await PreviewAsync(app, app.AdultA, property), "insurance");
        await AssertRefusedAsync(await DeleteAsync(app, property, await DeletionVersionAsync(app, property)), HttpStatusCode.Conflict, ItemCodes.InUse);

        Assert.Equal(1, blocker.GetProperty("count").GetInt32());
        var named = Assert.Single(blocker.GetProperty("items").EnumerateArray());
        Assert.Equal((subscription.Value, "Assurance habitation"), (named.GetProperty("id").GetGuid(), named.GetProperty("name").GetString()));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(app, property));
    }

    [Fact]
    public async Task A_private_insurance_of_another_adult_blocks_without_its_name()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var property = await NewPropertyAsync(app, Home(app, owners: [app.AdultA, app.AdultB]));
        var subscription = await app.NewOwnedSubscriptionAsync("Assurance de Bob", Visibility.Private, (app.AdultB, 10_000));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Put, $"/api/v1/subscriptions/{subscription.Value}/insurance",
            new { kind = "home", propertyId = property, guarantees = Array.Empty<object>() })).StatusCode);

        var blocker = Blocker(await PreviewAsync(app, app.AdultA, property), "insurance");

        Assert.Equal(1, blocker.GetProperty("count").GetInt32());
        Assert.Empty(blocker.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Stale_version_answers_409()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var property = await NewPropertyAsync(app, Home(app));
        var stale = await DeletionVersionAsync(app, property);
        var renamed = await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Properties}/{property}", new { name = "Maison de Léa", version = await VersionAsync(app, property) });
        Assert.True(renamed.IsSuccessStatusCode, await renamed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        await AssertRefusedAsync(await DeleteAsync(app, property, stale), HttpStatusCode.Conflict, ItemCodes.Changed);

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(app, property));
    }

    [Fact]
    public async Task Missing_version_answers_422()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var property = await NewPropertyAsync(app, Home(app));

        var response = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Properties}/{property}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("version", (await JsonAsync(response)).GetProperty("errors")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Reader_gets_403_and_stranger_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await NewPropertyAsync(app, Home(app));
        var secret = await NewPropertyAsync(app, Home(app, "Cabanon", "private"));

        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Properties}/{shared}/deletion")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{Properties}/{shared}?version=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Properties}/{secret}/deletion")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{Properties}/{secret}?version=1")).StatusCode);
    }

    [Fact]
    public async Task Write_direct_token_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var property = await NewPropertyAsync(app, Home(app));
        var version = await DeletionVersionAsync(app, property);
        var token = await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Direct);

        await AssertRefusedAsync(await app.BearerAsync(token.Secret, HttpMethod.Delete, $"{Properties}/{property}?version={version}"),
            HttpStatusCode.Forbidden, BrowserOnlyCodes.Required);

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(app, property));
    }
}
