using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Infrastructure.Catalog;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Catalog;

/// <summary>1C2c design §7: the server gives the icon's relative path; the web never builds one.</summary>
public class ProviderIconViewTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record ProviderResponse(string Key, string? IconUrl);
    public sealed record RowResponse(Guid Id, ProviderResponse? Provider);
    public sealed record ListResponse(List<RowResponse> Items);
    public sealed record DetailResponse(Guid Id, ProviderResponse? Provider);

    private static byte[] Webp() => IconProcessor.Process(IconSamples.Png(64, 64)).Ok().Webp;

    private static async Task<Guid> CreateAsync(NidApp app, string providerKey)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions", new
        {
            displayName = providerKey,
            visibility = "shared",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            providerKey,
            autoRenew = true,
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task The_provider_list_gives_a_stored_icon_its_url_on_nids_origin()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedProviderAsync("netflix", "Netflix", ProviderOrigin.Catalog);
        await app.SeedProviderAsync("spotify", "Spotify", ProviderOrigin.Catalog); // no icon stored: the monogram
        var hash = await app.SeedProviderIconAsync("netflix", Webp());

        var providers = await app.GetAsync<List<ProviderResponse>>(app.AdultB, "/api/v1/subscriptions/providers");

        var iconUrl = Assert.Single(providers, p => p.Key == "netflix").IconUrl;
        Assert.Equal($"/api/v1/subscriptions/providers/icons/{hash}", iconUrl);
        Assert.Null(Assert.Single(providers, p => p.Key == "spotify").IconUrl);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Get, iconUrl!)).StatusCode); // the URL works as given
    }

    [Fact]
    public async Task A_declared_icon_not_yet_stored_has_no_url()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedProviderAsync("netflix", "Netflix", ProviderOrigin.Catalog);
        await using (var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString))
            await subs.Providers.Where(p => p.Key == "netflix")
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.IconUrl, "https://1000logos.net/netflix.png"), cancellationToken: TestContext.Current.CancellationToken);

        var providers = await app.GetAsync<List<ProviderResponse>>(app.AdultA, "/api/v1/subscriptions/providers");

        Assert.Null(Assert.Single(providers, p => p.Key == "netflix").IconUrl);
    }

    [Fact]
    public async Task A_custom_provider_has_no_icon_url()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions/providers",
            new { name = "Tinder", labelKeys = new[] { CatalogLimits.OtherLabel } });

        var created = (await response.Content.ReadFromJsonAsync<ProviderResponse>(Json, cancellationToken: TestContext.Current.CancellationToken))!;

        Assert.Null(created.IconUrl);
        Assert.Contains("\"iconUrl\":null", await app.GetRawAsync(app.AdultA, "/api/v1/subscriptions/providers"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_subscription_row_and_detail_carry_their_providers_icon_url()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedProviderAsync("netflix", "Netflix", ProviderOrigin.Catalog);
        var hash = await app.SeedProviderIconAsync("netflix", Webp());
        var id = await CreateAsync(app, "netflix");
        var expected = $"/api/v1/subscriptions/providers/icons/{hash}";

        var list = await app.GetAsync<ListResponse>(app.AdultB, "/api/v1/subscriptions");
        var detail = await app.GetAsync<DetailResponse>(app.AdultB, $"/api/v1/subscriptions/{id}");

        Assert.Equal(expected, Assert.Single(list.Items, i => i.Id == id).Provider?.IconUrl);
        Assert.Equal(expected, detail.Provider?.IconUrl);
    }
}
