using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Catalog;

/// <summary>Design §3.3: a custom key is generated, never chosen, so it can neither collide with nor reveal another one.</summary>
[Collection("postgres")]
public class ProviderCreationTests(PostgresFixture fixture)
{
    private sealed class QueuedSuffixes(params string[] suffixes) : IProviderKeySuffix
    {
        private readonly Queue<string> queue = new(suffixes);

        public string Next() => queue.Dequeue();
    }

    private static async Task<(ProviderView? View, string? Code)> CreateAsync(NidApp app, CreateProviderCommand command)
    {
        var (created, failure) = await app.InvokeAsync(app.AdultA, command);
        return ((ProviderView?)created, failure?.Code);
    }

    [Fact]
    public async Task A_custom_key_is_the_slug_of_the_name_and_six_random_characters()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var (view, code) = await CreateAsync(app, new CreateProviderCommand("Tinder", "https://tinder.com", null, [CatalogLimits.OtherLabel]));

        Assert.Null(code);
        Assert.Matches("^tinder-[a-z0-9]{6}$", view!.Key);
        Assert.Equal(("Tinder", "custom", "https://tinder.com", false), (view.Name, view.Origin, view.Website, view.Archived));
        Assert.Equal([new LabelView(CatalogLimits.OtherLabel, "Other", CatalogLimits.NeutralColor)], view.Labels);
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        Assert.Equal(app.AdultA.Value, (await subs.Providers.AsNoTracking().SingleAsync(p => p.Key == view.Key)).CreatedBy);
    }

    [Fact]
    public async Task Two_creations_of_the_same_name_give_two_keys()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var command = new CreateProviderCommand("Tinder", null, null, [CatalogLimits.OtherLabel]);

        var (first, _) = await CreateAsync(app, command);
        var (second, _) = await CreateAsync(app, command);

        Assert.NotEqual(first!.Key, second!.Key);
    }

    [Fact]
    public async Task A_generated_key_never_takes_one_that_exists()
    {
        await using var app = await NidApp.StartAsync(fixture, services => services.AddSingleton<IProviderKeySuffix>(new QueuedSuffixes("aaaaaa", "bbbbbb")));
        await app.SeedProviderAsync("tinder-aaaaaa", "Tinder", ProviderOrigin.Catalog);

        var (view, _) = await CreateAsync(app, new CreateProviderCommand("Tinder", null, null, [CatalogLimits.OtherLabel]));

        Assert.Equal("tinder-bbbbbb", view!.Key);
    }

    [Fact]
    public async Task A_name_with_nothing_to_slug_gets_a_neutral_key()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (view, _) = await CreateAsync(app, new CreateProviderCommand("🎬🍿", null, null, [CatalogLimits.OtherLabel]));
        Assert.Matches("^provider-[a-z0-9]{6}$", view!.Key);
        Assert.Equal("🎬🍿", view.Name);
    }

    [Fact]
    public async Task Blank_addresses_count_as_absent()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (view, code) = await CreateAsync(app, new CreateProviderCommand(" Tinder ", "  ", "", [CatalogLimits.OtherLabel]));
        Assert.Null(code);
        Assert.Equal(("Tinder", (string?)null), (view!.Name, view.Website));
    }

    public static TheoryData<string?, string?, string?, string[]> Invalid => new()
    {
        { null, null, null, [CatalogLimits.OtherLabel] },
        { "   ", null, null, [CatalogLimits.OtherLabel] },
        { new string('n', 101), null, null, [CatalogLimits.OtherLabel] },
        { "Tinder", "ftp://tinder.com", null, [CatalogLimits.OtherLabel] },
        { "Tinder", "tinder.com", null, [CatalogLimits.OtherLabel] },
        { "Tinder", null, "javascript:alert(1)", [CatalogLimits.OtherLabel] },
        { "Tinder", null, null, [] },
        { "Tinder", null, null, ["nope"] },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public async Task An_invalid_provider_is_refused_and_nothing_is_created(string? name, string? website, string? pricingPage, string[] labels)
    {
        await using var app = await NidApp.StartAsync(fixture);

        Assert.Equal(CatalogCodes.InvalidProvider, (await CreateAsync(app, new CreateProviderCommand(name, website, pricingPage, labels))).Code);

        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        Assert.False(await subs.Providers.AnyAsync());
    }
}
