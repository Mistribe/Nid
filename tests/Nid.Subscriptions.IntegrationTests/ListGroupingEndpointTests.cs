using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests;

[Collection("postgres")]
public class ListGroupingEndpointTests(PostgresFixture fixture)
{
    public sealed record GroupResponse(string Key, string? Name, List<Guid> ItemIds);
    public sealed record ListResponse(List<GroupResponse> Groups);

    [Fact]
    public async Task The_list_groups_by_provider_on_request()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedProviderAsync("acme", "Acme", ProviderOrigin.Custom, app.AdultA);
        var first = await app.NewSubscriptionAsync("One", app.AdultA, Visibility.Shared);
        var second = await app.NewSubscriptionAsync("Two", app.AdultA, Visibility.Shared);
        await app.LinkProviderAsync(first, "acme");
        await app.LinkProviderAsync(second, "acme");

        var grouped = await app.GetAsync<ListResponse>(app.AdultA, "/api/v1/subscriptions?group=provider");
        var plain = await app.GetAsync<ListResponse>(app.AdultA, "/api/v1/subscriptions");

        var group = Assert.Single(grouped.Groups);
        Assert.Equal("acme", group.Key);
        Assert.Equal("Acme", group.Name);
        Assert.Equal([first.Value, second.Value], group.ItemIds.Order());
        Assert.Empty(plain.Groups);
    }
}
