using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Catalog;

/// <summary>Design §5: a new link names a provider visible to the actor and not archived, or it is the same 422.</summary>
public class ProviderLinkTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static Task<HttpResponseMessage> CreateAsync(NidApp app, PersonId owner, string? providerKey, Visibility visibility = Visibility.Shared)
        => app.RawAsync(owner, HttpMethod.Post, "/api/v1/subscriptions", new
        {
            displayName = "linked",
            visibility = visibility.ToString().ToLowerInvariant(),
            owners = new[] { new { personId = owner.Value, shareBp = 10_000 } },
            providerKey,
            autoRenew = true,
        });

    private static async Task<ItemId> CreatedAsync(NidApp app, PersonId owner, string? providerKey, Visibility visibility = Visibility.Shared)
    {
        var response = await CreateAsync(app, owner, providerKey, visibility);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return new ItemId((await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid());
    }

    private static Task<HttpResponseMessage> EditAsync(NidApp app, PersonId actor, ItemId item, object body)
        => app.RawAsync(actor, HttpMethod.Patch, $"/api/v1/subscriptions/{item.Value}", body);

    private static async Task<(HttpStatusCode Status, string? Code, string? Title)> ProblemOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return (response.StatusCode, body.GetProperty("code").GetString(), body.GetProperty("title").GetString());
    }

    private static async Task<string?> StoredKeyAsync(NidApp app, ItemId item)
    {
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        return (await subs.Subscriptions.AsNoTracking().SingleAsync(s => s.ItemId == item.Value)).ProviderKey;
    }

    [Fact]
    public async Task An_unknown_an_invisible_and_an_archived_provider_are_refused_alike()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedProviderAsync("hulu", "Hulu", ProviderOrigin.Catalog, archived: true);
        var hidden = await ProviderVisibilityTests.CreateCustomAsync(app, app.AdultA, "Tinder"); // A's alone: unused

        var answers = new List<(HttpStatusCode, string?, string?)>();
        foreach (var key in new[] { "nope", hidden, "hulu" })
            answers.Add(await ProblemOf(await CreateAsync(app, app.AdultB, key)));

        Assert.All(answers, answer => Assert.Equal((HttpStatusCode.UnprocessableEntity, CatalogCodes.UnknownProvider, answers[0].Item3), answer));
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.False(await core.Items.AnyAsync()); // refused before the Core item is created
    }

    [Fact]
    public async Task A_visible_provider_links_on_creation_and_on_edit()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedProviderAsync("netflix", "Netflix", ProviderOrigin.Catalog);

        var created = await CreatedAsync(app, app.AdultA, "netflix");
        var edited = await CreatedAsync(app, app.AdultA, null);
        (await EditAsync(app, app.AdultA, edited, new { providerKey = "netflix" })).EnsureSuccessStatusCode();

        Assert.Equal(("netflix", "netflix"), (await StoredKeyAsync(app, created), await StoredKeyAsync(app, edited)));
    }

    [Fact]
    public async Task A_subscription_keeps_its_archived_provider_and_only_a_new_link_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedProviderAsync("hulu", "Hulu", ProviderOrigin.Catalog);
        var item = await CreatedAsync(app, app.AdultA, "hulu");
        await using (var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString))
            await subs.Providers.Where(p => p.Key == "hulu").ExecuteUpdateAsync(s => s.SetProperty(p => p.ArchivedAt, (NodaTime.Instant?)NodaTime.SystemClock.Instance.GetCurrentInstant()));

        (await EditAsync(app, app.AdultA, item, new { note = "still here" })).EnsureSuccessStatusCode();
        (await EditAsync(app, app.AdultA, item, new { providerKey = "hulu" })).EnsureSuccessStatusCode(); // not a new link
        Assert.Equal("hulu", await StoredKeyAsync(app, item));

        Assert.Equal(CatalogCodes.UnknownProvider, (await ProblemOf(await CreateAsync(app, app.AdultA, "hulu"))).Code);
    }

    [Fact]
    public async Task Linking_a_shared_subscription_to_a_custom_provider_shows_it_to_the_other_adult()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var tinder = await ProviderVisibilityTests.CreateCustomAsync(app, app.AdultA, "Tinder");
        Assert.DoesNotContain(tinder, await ProviderVisibilityTests.VisibleAsync(app, app.AdultB));

        var shared = await CreatedAsync(app, app.AdultA, null);
        (await EditAsync(app, app.AdultA, shared, new { providerKey = tinder })).EnsureSuccessStatusCode();

        Assert.Contains(tinder, await ProviderVisibilityTests.VisibleAsync(app, app.AdultB));
    }

    /// <summary>Review Focus 1.</summary>
    [Fact]
    public async Task A_custom_provider_left_unused_is_visible_to_its_creator_only_again()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedProviderAsync("netflix", "Netflix", ProviderOrigin.Catalog);
        var custom = await ProviderVisibilityTests.CreateCustomAsync(app, app.AdultA, "Mon Netflix");
        var shared = await CreatedAsync(app, app.AdultA, custom);
        Assert.Contains(custom, await ProviderVisibilityTests.VisibleAsync(app, app.AdultB));

        (await EditAsync(app, app.AdultA, shared, new { providerKey = "netflix" })).EnsureSuccessStatusCode();

        Assert.Contains(custom, await ProviderVisibilityTests.VisibleAsync(app, app.AdultA));
        Assert.DoesNotContain(custom, await ProviderVisibilityTests.VisibleAsync(app, app.AdultB));
    }

    [Fact]
    public async Task A_provider_is_removed_with_clearProviderKey_and_both_at_once_is_a_bad_request()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedProviderAsync("netflix", "Netflix", ProviderOrigin.Catalog);
        var item = await CreatedAsync(app, app.AdultA, "netflix");

        Assert.Equal(HttpStatusCode.BadRequest, (await EditAsync(app, app.AdultA, item, new { providerKey = "netflix", clearProviderKey = true })).StatusCode);
        (await EditAsync(app, app.AdultA, item, new { clearProviderKey = true })).EnsureSuccessStatusCode();

        Assert.Null(await StoredKeyAsync(app, item));
    }
}
