using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;
using Nid.TestSupport;
using static Nid.TestSupport.ProviderApi;

namespace Nid.Subscriptions.IntegrationTests.Catalog;

/// <summary>Design §3.5, computed from IVisibleItems: a provider can say no more than the subscriptions that use it.</summary>
public class ProviderVisibilityTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_custom_provider_of_a_private_subscription_is_invisible_to_the_other_adult()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var tinder = await CreateCustomAsync(app, app.AdultA, "Tinder");
        await app.LinkProviderAsync(await app.NewSubscriptionAsync("Tinder Gold", app.AdultA, Visibility.Private), tinder);

        Assert.Contains(tinder, await VisibleAsync(app, app.AdultA));
        Assert.DoesNotContain(tinder, await VisibleAsync(app, app.AdultB));
    }

    [Fact]
    public async Task It_becomes_visible_to_the_other_adult_once_a_shared_subscription_uses_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var tinder = await CreateCustomAsync(app, app.AdultA, "Tinder");
        await app.LinkProviderAsync(await app.NewSubscriptionAsync("Tinder Gold", app.AdultA, Visibility.Private), tinder);
        await app.LinkProviderAsync(await app.NewSubscriptionAsync("Tinder for two", app.AdultA, Visibility.Shared), tinder);

        Assert.Contains(tinder, await VisibleAsync(app, app.AdultB));
    }

    [Fact]
    public async Task A_custom_provider_nobody_uses_is_visible_to_its_creator_only()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var tinder = await CreateCustomAsync(app, app.AdultA, "Tinder");

        Assert.Contains(tinder, await VisibleAsync(app, app.AdultA));
        Assert.DoesNotContain(tinder, await VisibleAsync(app, app.AdultB));
    }

    /// <summary>Design §3.5 as amended: <c>IVisibleItems</c> leaves archived items out, which must not hide a provider from its creator.</summary>
    [Fact]
    public async Task A_custom_provider_whose_only_subscription_is_archived_stays_visible_to_its_creator()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var tinder = await CreateCustomAsync(app, app.AdultA, "Tinder");
        var shared = await app.NewSubscriptionAsync("Tinder for two", app.AdultA, Visibility.Shared);
        await app.LinkProviderAsync(shared, tinder);
        Assert.Contains(tinder, await VisibleAsync(app, app.AdultB));

        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
            await core.Items.Where(i => i.Id == shared.Value)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.ArchivedAt, (NodaTime.Instant?)NodaTime.SystemClock.Instance.GetCurrentInstant()));

        Assert.Contains(tinder, await VisibleAsync(app, app.AdultA));
        Assert.DoesNotContain(tinder, await VisibleAsync(app, app.AdultB));
    }

    [Fact]
    public async Task Catalog_providers_are_visible_to_everyone_and_adopted_ones_through_their_subscriptions()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedProviderAsync("netflix", "Netflix", ProviderOrigin.Catalog);
        await app.SeedProviderAsync("wow", "Wow", ProviderOrigin.Adopted);

        foreach (var adult in new[] { app.AdultA, app.AdultB })
            Assert.Equal(["netflix"], await VisibleAsync(app, adult));

        await app.LinkProviderAsync(await app.NewSubscriptionAsync("World of Warcraft", app.AdultA, Visibility.Shared), "wow");

        foreach (var adult in new[] { app.AdultA, app.AdultB })
            Assert.Equal(["netflix", "wow"], (await VisibleAsync(app, adult)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_list_filters_by_name_ignoring_case_and_takes_wildcards_literally()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedProviderAsync("netflix", "Netflix", ProviderOrigin.Catalog);
        await app.SeedProviderAsync("disney-plus", "Disney+", ProviderOrigin.Catalog);
        await app.SeedProviderAsync("fifty", "50% Off", ProviderOrigin.Catalog);

        Assert.Equal(["netflix"], await VisibleAsync(app, app.AdultA, "NET"));
        Assert.Equal(["fifty"], await VisibleAsync(app, app.AdultA, "%"));
        Assert.Empty(await VisibleAsync(app, app.AdultA, "_x")); // "_" is not a wildcard: "lix" does not match
        Assert.Equal(3, (await VisibleAsync(app, app.AdultA, "  ")).Count); // blank means no filter
    }

    [Fact]
    public async Task Searching_for_the_name_of_the_other_adults_private_custom_provider_finds_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var tinder = await CreateCustomAsync(app, app.AdultA, "Tinder");
        await app.LinkProviderAsync(await app.NewSubscriptionAsync("Tinder Gold", app.AdultA, Visibility.Private), tinder);

        Assert.Equal([tinder], await VisibleAsync(app, app.AdultA, "tind"));
        Assert.Empty(await VisibleAsync(app, app.AdultB, "tind"));
    }

    [Fact]
    public async Task An_adopted_provider_only_a_private_subscription_uses_is_hidden_from_the_other_adult()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedProviderAsync("wow", "Wow", ProviderOrigin.Adopted);
        await app.LinkProviderAsync(await app.NewSubscriptionAsync("World of Warcraft", app.AdultA, Visibility.Private), "wow");

        Assert.Equal(["wow"], await VisibleAsync(app, app.AdultA));
        Assert.Empty(await VisibleAsync(app, app.AdultB));
    }

    [Fact]
    public async Task The_other_adults_private_subscription_using_a_custom_provider_changes_nothing_its_creator_sees()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var tinder = await CreateCustomAsync(app, app.AdultA, "Tinder");
        var before = await VisibleAsync(app, app.AdultA);
        var beforeSearch = await VisibleAsync(app, app.AdultA, "tind");

        await app.LinkProviderAsync(await app.NewSubscriptionAsync("Tinder Gold", app.AdultB, Visibility.Private), tinder);

        Assert.Equal([tinder], before);
        Assert.Equal(before, await VisibleAsync(app, app.AdultA));
        Assert.Equal(beforeSearch, await VisibleAsync(app, app.AdultA, "tind"));
    }

    [Fact]
    public async Task Every_label_is_listed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (labels, failure) = await app.InvokeAsync(app.AdultB, new ListLabelsQuery());

        Assert.Null(failure);
        Assert.Equal([new LabelView(CatalogLimits.OtherLabel, "Other", CatalogLimits.NeutralColor)], (IReadOnlyList<LabelView>)labels!);
    }
}
