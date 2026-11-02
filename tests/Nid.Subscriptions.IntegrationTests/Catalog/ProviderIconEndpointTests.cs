using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Infrastructure.Catalog;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Catalog;

/// <summary>1C2c design §6.</summary>
public class ProviderIconEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Icons = IconLimits.PathPrefix;

    private static byte[] Webp(string color = "#6400F6") => IconProcessor.Process(IconSamples.Png(64, 64, color)).Ok().Webp;

    /// <summary>A catalog provider <c>netflix</c> with a stored icon: its hash and its bytes.</summary>
    private static async Task<(string Hash, byte[] Content)> SeedNetflixIconAsync(NidApp app)
    {
        await app.SeedProviderAsync("netflix", "Netflix", ProviderOrigin.Catalog);
        var content = Webp();
        return (await app.SeedProviderIconAsync("netflix", content), content);
    }

    [Fact]
    public async Task A_catalog_icon_is_served_with_its_headers_to_any_adult()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (hash, content) = await SeedNetflixIconAsync(app);

        var response = await app.RawAsync(app.AdultB, HttpMethod.Get, Icons + hash);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(content, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal($"\"{hash}\"", response.Headers.ETag?.Tag);
        var cache = response.Headers.CacheControl!;
        Assert.True(cache.Private);
        Assert.Equal(TimeSpan.FromSeconds(31_536_000), cache.MaxAge);
        Assert.Contains(cache.Extensions, e => e.Name == "immutable");
        Assert.Equal(["nosniff"], response.Headers.GetValues("X-Content-Type-Options"));
        Assert.Equal(["default-src 'none'; sandbox"], response.Headers.GetValues("Content-Security-Policy"));
    }

    [Theory]
    [InlineData("\"{0}\"")]
    [InlineData("W/\"{0}\"")]
    [InlineData("\"other\", \"{0}\"")]
    [InlineData("*")]
    public async Task An_If_None_Match_naming_the_hash_answers_304(string ifNoneMatch)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (hash, _) = await SeedNetflixIconAsync(app);

        var response = await app.GetWithHeadersAsync(app.AdultA, Icons + hash,
            new Dictionary<string, string> { ["If-None-Match"] = string.Format(System.Globalization.CultureInfo.InvariantCulture, ifNoneMatch, hash) });

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal($"\"{hash}\"", response.Headers.ETag?.Tag);
        // Same three headers as the 200 (brief §6): a 304 still carries the ETag, the full Cache-Control and nosniff.
        var cache = response.Headers.CacheControl!;
        Assert.True(cache.Private);
        Assert.Equal(TimeSpan.FromSeconds(31_536_000), cache.MaxAge);
        Assert.Contains(cache.Extensions, e => e.Name == "immutable");
        Assert.Equal(["nosniff"], response.Headers.GetValues("X-Content-Type-Options"));
    }

    [Fact]
    public async Task An_If_None_Match_naming_another_version_answers_the_icon()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (hash, _) = await SeedNetflixIconAsync(app);

        var response = await app.GetWithHeadersAsync(app.AdultA, Icons + hash,
            new Dictionary<string, string> { ["If-None-Match"] = $"\"{new string('b', 64)}\"" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_icon_needs_a_session()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (hash, _) = await SeedNetflixIconAsync(app);

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.RawAnonymousAsync(HttpMethod.Get, Icons + hash)).StatusCode);
    }

    public static TheoryData<string> NotIcons => new()
    {
        new string('0', 64),                  // well formed, stored nowhere
        new string('A', 64),                  // not the store's spelling
        "not-a-hash",
        new string('a', 65),
        "..%2F..%2Fappsettings.json",
    };

    [Theory]
    [MemberData(nameof(NotIcons))]
    public async Task An_unknown_or_malformed_hash_is_404(string hash)
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SeedNetflixIconAsync(app);

        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, Icons + hash)).StatusCode);
    }

    /// <summary>Design §6: the route serves catalog icons, never whatever else the asset store may hold later.</summary>
    [Fact]
    public async Task A_stored_file_that_is_no_catalog_icon_is_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SeedNetflixIconAsync(app);
        var loose = await app.Services.GetRequiredService<IAssetStore>().PutAsync(Webp("#F06292"), "image/webp", TestContext.Current.CancellationToken);
        await app.SeedProviderAsync("tinder-k3f9qa", "Tinder", ProviderOrigin.Custom);
        var custom = await app.SeedProviderIconAsync("tinder-k3f9qa", Webp("#FFD54F")); // a state no write produces

        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, Icons + loose)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, Icons + custom)).StatusCode);
    }

    /// <summary>As <see cref="A_stored_file_that_is_no_catalog_icon_is_404"/>, for the other non-catalog origin (design §3.4).</summary>
    [Fact]
    public async Task A_hash_linked_only_to_an_adopted_provider_is_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SeedNetflixIconAsync(app);
        await app.SeedProviderAsync("disney-plus-z8n2wq", "Disney+", ProviderOrigin.Adopted);
        var adopted = await app.SeedProviderIconAsync("disney-plus-z8n2wq", Webp("#4FC3F7")); // a state no write produces

        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, Icons + adopted)).StatusCode);
    }

    [Fact]
    public async Task A_linked_icon_whose_file_is_gone_is_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (hash, _) = await SeedNetflixIconAsync(app);
        File.Delete(Path.Combine(app.AssetsRoot, hash[..2], hash + ".webp")); // a volume restored without its assets

        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, Icons + hash)).StatusCode);
    }

    [Fact]
    public async Task An_archived_catalog_providers_icon_is_still_served()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (hash, _) = await SeedNetflixIconAsync(app);
        await using (var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString))
            await subs.Providers.Where(p => p.Key == "netflix")
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ArchivedAt, (NodaTime.Instant?)NodaTime.SystemClock.Instance.GetCurrentInstant()), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Get, Icons + hash)).StatusCode);
    }
}
