using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Infrastructure.Persistence;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Catalog;

public class ProviderStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static Provider Custom(string key, PersonId creator)
        => new(key, ProviderOrigin.Custom, "Tinder", "https://tinder.com", null, [CatalogLimits.OtherLabel], null, creator, null, null);

    private async Task<ProviderStore> NewStoreAsync()
        => new(PostgresFixture.NewSubscriptionsContext(await fixture.NewConnectionStringAsync()));

    [Fact]
    public async Task The_local_label_is_read_back()
    {
        var store = await NewStoreAsync();
        Assert.Equal(new Label(CatalogLimits.OtherLabel, "Other", CatalogLimits.NeutralColor, LabelOrigin.Local), Assert.Single(await store.LabelsAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_custom_provider_round_trips()
    {
        var store = await NewStoreAsync();
        var provider = Custom("tinder-k3f9qa", PersonId.New());

        (await store.CreateAsync(provider, TestContext.Current.CancellationToken)).Ok();

        Assert.Equal(provider, (await store.FindAsync(["tinder-k3f9qa", "absent"], TestContext.Current.CancellationToken))["tinder-k3f9qa"]);
        Assert.True(await store.ExistsAsync("tinder-k3f9qa", TestContext.Current.CancellationToken));
        Assert.False(await store.ExistsAsync("absent", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_key_already_held_is_refused_as_taken()
    {
        var store = await NewStoreAsync();
        (await store.CreateAsync(Custom("tinder-k3f9qa", PersonId.New()), TestContext.Current.CancellationToken)).Ok();

        Assert.Equal(CatalogCodes.KeyTaken, (await store.CreateAsync(Custom("tinder-k3f9qa", PersonId.New()), TestContext.Current.CancellationToken)).FailureCode());
    }
}
