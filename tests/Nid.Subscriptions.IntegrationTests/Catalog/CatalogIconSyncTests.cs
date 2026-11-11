using Nid.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nid.Platform.Contracts;
using Nid.Platform.Infrastructure.Assets;
using UnambitiousFx.Myelin;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Infrastructure.Catalog;
using Nid.Subscriptions.Infrastructure.Persistence;
using Nid.TestSupport;
using UnambitiousFx.Functional;

namespace Nid.Subscriptions.IntegrationTests.Catalog;

/// <summary>1C2c design §5: the icon pass after the merge, through <see cref="NidApp.Icons"/> unless a test says otherwise.</summary>
public class CatalogIconSyncTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string NetflixIcon = "https://1000logos.net/netflix.png";
    private const string SpotifyIcon = "https://upload.wikimedia.org/spotify.png";

    private static void ServeCatalog(NidApp app, string? netflixIcon = NetflixIcon, string? spotifyIcon = SpotifyIcon)
        => app.Catalog.Serve(CatalogFixtures.StreamingLabels(), CatalogFixtures.Providers(("netflix", "Netflix", netflixIcon), ("spotify", "Spotify", spotifyIcon)));

    private static async Task<Result<CatalogSyncRun>> RunAsync(NidApp app, CancellationToken ct = default)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System; // the catalog job runs as the system (1F3a U4)
        return await scope.ServiceProvider.GetRequiredService<CatalogSync>().RunAsync(ct);
    }

    private static async Task<IconReport> SyncAsync(NidApp app)
    {
        var run = (await RunAsync(app)).Ok();
        Assert.Equal(CatalogRunStatus.Succeeded, run.Status);
        return run.Report!.Icons;
    }

    private static async Task<ProviderRow> RowAsync(NidApp app, string key)
    {
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        return await subs.Providers.AsNoTracking().SingleAsync(p => p.Key == key);
    }

    private static string FileOf(NidApp app, string hash) => Path.Combine(app.AssetsRoot, hash[..2], hash + ".webp");

    [Fact]
    public async Task An_icon_is_fetched_stored_linked_and_journaled_and_a_failure_leaves_the_monogram()
    {
        await using var app = await NidApp.StartAsync(fixture);
        ServeCatalog(app);
        var png = IconSamples.Png(1024, 512);
        app.Icons.Serve(NetflixIcon, png);

        var icons = await SyncAsync(app);

        Assert.Equal(["netflix"], icons.Fetched);
        Assert.Equal(0, icons.Unchanged);
        Assert.Equal([new IconRefusal("spotify", IconCodes.Unreachable)], icons.Refused);
        var netflix = await RowAsync(app, "netflix");
        var expected = IconProcessor.Process(png).Ok();
        Assert.Equal((expected.Hash, NetflixIcon), (netflix.IconAsset, netflix.IconSource));
        Assert.Equal(expected.Webp, await File.ReadAllBytesAsync(FileOf(app, expected.Hash), TestContext.Current.CancellationToken));
        Assert.Null((await RowAsync(app, "spotify")).IconAsset); // the monogram

        await using var scope = app.Services.CreateAsyncScope();
        var journaled = Assert.Single(await scope.ServiceProvider.GetRequiredService<ICatalogRunLog>().RecentAsync(10, TestContext.Current.CancellationToken));
        Assert.Equal(["netflix"], journaled.Report!.Icons.Fetched);
        Assert.Equal([new IconRefusal("spotify", IconCodes.Unreachable)], journaled.Report.Icons.Refused);
    }

    [Fact]
    public async Task An_unchanged_icon_is_not_downloaded_again_and_a_failed_one_is_retried()
    {
        await using var app = await NidApp.StartAsync(fixture);
        ServeCatalog(app);
        app.Icons.Serve(NetflixIcon, IconSamples.Png(64, 64));
        await SyncAsync(app);
        app.Icons.Serve(SpotifyIcon, IconSamples.Png(64, 64, "#F06292")); // Wikimedia is back

        var second = await SyncAsync(app);

        Assert.Equal(["spotify"], second.Fetched);
        Assert.Equal(1, second.Unchanged);
        Assert.Empty(second.Refused);
        Assert.Equal(1, app.Icons.Requested.Count(u => u == NetflixIcon));
        Assert.Equal(2, app.Icons.Requested.Count(u => u == SpotifyIcon));
    }

    [Fact]
    public async Task A_changed_icon_url_is_downloaded_and_replaces_the_icon()
    {
        await using var app = await NidApp.StartAsync(fixture);
        ServeCatalog(app, spotifyIcon: null);
        app.Icons.Serve(NetflixIcon, IconSamples.Png(64, 64));
        await SyncAsync(app);
        const string moved = "https://upload.wikimedia.org/netflix.png";
        ServeCatalog(app, netflixIcon: moved, spotifyIcon: null);
        var replacement = IconSamples.Png(64, 64, "#F06292");
        app.Icons.Serve(moved, replacement);

        var icons = await SyncAsync(app);

        Assert.Equal(["netflix"], icons.Fetched);
        var netflix = await RowAsync(app, "netflix");
        Assert.Equal((IconProcessor.Process(replacement).Ok().Hash, moved), (netflix.IconAsset, netflix.IconSource));
    }

    [Fact]
    public async Task A_failed_icon_keeps_the_previous_one_until_a_later_sync_succeeds()
    {
        await using var app = await NidApp.StartAsync(fixture);
        ServeCatalog(app, spotifyIcon: null);
        var original = IconSamples.Png(64, 64);
        app.Icons.Serve(NetflixIcon, original);
        await SyncAsync(app);
        const string moved = "https://upload.wikimedia.org/netflix.png";
        ServeCatalog(app, netflixIcon: moved, spotifyIcon: null);
        app.Icons.Fail(moved, IconCodes.HttpStatus);

        var failed = await SyncAsync(app);

        Assert.Equal([new IconRefusal("netflix", IconCodes.HttpStatus)], failed.Refused);
        var kept = await RowAsync(app, "netflix");
        Assert.Equal((IconProcessor.Process(original).Ok().Hash, NetflixIcon, moved), (kept.IconAsset, kept.IconSource, kept.IconUrl));

        app.Icons.Serve(moved, IconSamples.Png(64, 64, "#F06292"));
        Assert.Equal(["netflix"], (await SyncAsync(app)).Fetched);
        Assert.Equal(moved, (await RowAsync(app, "netflix")).IconSource);
    }

    [Fact]
    public async Task An_icon_on_a_host_outside_the_list_is_refused_and_the_sync_succeeds()
    {
        var handler = new RecordingHandler();
        await using var app = await NidApp.StartAsync(fixture, services => services.AddSingleton<ICatalogIconSource>(new CatalogIconSource(
            new SafeHttpClient(new HttpMessageInvoker(handler), NidOutbound.Options(), NullLogger<SafeHttpClient>.Instance))));
        ServeCatalog(app, netflixIcon: "https://cdn.example.com/netflix.png", spotifyIcon: null);

        var icons = await SyncAsync(app);

        Assert.Equal([new IconRefusal("netflix", IconCodes.HostNotAllowed)], icons.Refused);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task An_icon_the_catalog_gets_wrong_is_reported_and_never_fetched()
    {
        await using var app = await NidApp.StartAsync(fixture);
        ServeCatalog(app, netflixIcon: "http://1000logos.net/netflix.png", spotifyIcon: null);

        var run = (await RunAsync(app, TestContext.Current.CancellationToken)).Ok();

        Assert.Equal(["netflix", "spotify"], run.Report!.ProvidersAdded); // the provider is kept
        Assert.Equal([new IconRefusal("netflix", IconCodes.NotHttps)], run.Report.Icons.Refused);
        Assert.Empty(app.Icons.Requested);
    }

    /// <summary>Review Focus 5.</summary>
    [Fact]
    public async Task Two_providers_with_the_same_icon_share_one_file()
    {
        await using var app = await NidApp.StartAsync(fixture);
        ServeCatalog(app);
        var same = IconSamples.Png(64, 64);
        app.Icons.Serve(NetflixIcon, same);
        app.Icons.Serve(SpotifyIcon, same);

        Assert.Equal(["netflix", "spotify"], (await SyncAsync(app)).Fetched);

        Assert.Equal((await RowAsync(app, "netflix")).IconAsset, (await RowAsync(app, "spotify")).IconAsset);
        Assert.Single(Directory.GetFiles(app.AssetsRoot, "*", SearchOption.AllDirectories));
    }

    /// <summary>Review Focus 5.</summary>
    [Fact]
    public async Task An_unwritable_asset_volume_fails_the_icons_not_the_sync()
    {
        var blocker = Path.GetTempFileName(); // a file where the volume's folder should be
        try
        {
            await using var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string> { ["Assets:Root"] = Path.Combine(blocker, "assets") });
            ServeCatalog(app, spotifyIcon: null);
            app.Icons.Serve(NetflixIcon, IconSamples.Png(64, 64));

            var icons = await SyncAsync(app);

            Assert.Equal([new IconRefusal("netflix", IconCodes.StoreFailed)], icons.Refused);
            Assert.Null((await RowAsync(app, "netflix")).IconAsset);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public async Task A_shutdown_during_the_icon_pass_closes_the_run_as_interrupted_after_the_catalog_is_written()
    {
        await using var app = await NidApp.StartAsync(fixture);
        ServeCatalog(app);
        using var shutdown = new CancellationTokenSource();
        app.Icons.Before = async (_, ct) =>
        {
            await shutdown.CancelAsync(); // the host stops while the first icon is on its way
            await Task.Delay(Timeout.Infinite, ct);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(app, shutdown.Token));

        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        var run = await subs.CatalogSyncRuns.AsNoTracking().SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(("failed", CatalogCodes.RunInterrupted), (run.Status, run.FailureCode));
        Assert.Equal(2, await subs.Providers.CountAsync(p => p.Origin == "catalog", cancellationToken: TestContext.Current.CancellationToken)); // the merge had committed
        Assert.Equal([NetflixIcon], app.Icons.Requested); // the pass stopped at once
    }

    [Fact]
    public async Task An_archived_provider_is_not_fetched()
    {
        await using var app = await NidApp.StartAsync(fixture);
        ServeCatalog(app);
        await SyncAsync(app); // both unreachable
        app.Catalog.Serve(CatalogFixtures.StreamingLabels(), CatalogFixtures.Providers(("spotify", "Spotify", SpotifyIcon)));

        await SyncAsync(app); // netflix leaves the catalog: archived

        Assert.Equal(1, app.Icons.Requested.Count(u => u == NetflixIcon));
    }

    /// <summary>Review follow-up, Important 1: an exception the source never promised not to throw is this icon's refusal.</summary>
    [Fact]
    public async Task An_icon_source_that_throws_is_refused_and_the_next_icon_is_still_processed()
    {
        var inner = FakeIconSource.Offline();
        await using var app = await NidApp.StartAsync(fixture, services => services.AddSingleton<ICatalogIconSource>(new ThrowingIconSource(inner, NetflixIcon)));
        ServeCatalog(app);
        inner.Serve(SpotifyIcon, IconSamples.Png(64, 64));

        var icons = await SyncAsync(app);

        Assert.Equal(["spotify"], icons.Fetched);
        Assert.Equal([new IconRefusal("netflix", IconCodes.Unreachable)], icons.Refused);
    }

    /// <summary>Review follow-up, Important 1: an exception storing or linking is this icon's refusal, not a failed run.</summary>
    [Fact]
    public async Task A_store_or_link_that_throws_is_refused_and_the_next_icon_is_still_processed()
    {
        var netflixWebp = IconProcessor.Process(IconSamples.Png(64, 64)).Ok().Webp;
        await using var app = await NidApp.StartAsync(fixture, services => services.AddSingleton<IAssetStore>(
            sp => new ThrowingAssetStore(new FileAssetStore(sp.GetRequiredService<AssetOptions>()), netflixWebp)));
        ServeCatalog(app);
        app.Icons.Serve(NetflixIcon, IconSamples.Png(64, 64));
        app.Icons.Serve(SpotifyIcon, IconSamples.Png(64, 64, "#F06292"));

        var icons = await SyncAsync(app);

        Assert.Equal(["spotify"], icons.Fetched);
        Assert.Equal([new IconRefusal("netflix", IconCodes.StoreFailed)], icons.Refused);
    }

    /// <summary>Review follow-up, Minor 2: a catalog key a custom provider already holds never becomes a catalog
    /// provider, so the reader's refusal for its bad icon is not this pass's to report.</summary>
    [Fact]
    public async Task A_conflicting_keys_bad_icon_is_not_reported()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedProviderAsync("netflix", "My Netflix", ProviderOrigin.Custom, app.AdultA);
        ServeCatalog(app, netflixIcon: "http://1000logos.net/netflix.png", spotifyIcon: null); // not https: the reader refuses it

        var run = (await RunAsync(app, TestContext.Current.CancellationToken)).Ok();

        Assert.Equal(CatalogRunStatus.Succeeded, run.Status);
        Assert.Equal(["netflix"], run.Report!.Conflicts);
        Assert.Empty(run.Report.Icons.Refused);
    }

    private sealed class ThrowingIconSource(ICatalogIconSource inner, string throwFor) : ICatalogIconSource
    {
        public Task<Result<ProcessedIcon>> FetchAsync(string url, CancellationToken ct)
            => url == throwFor ? throw new InvalidOperationException("the icon source exploded") : inner.FetchAsync(url, ct);
    }

    private sealed class ThrowingAssetStore(IAssetStore inner, byte[] failFor) : IAssetStore
    {
        public Task<string> PutAsync(ReadOnlyMemory<byte> content, string contentType, CancellationToken ct)
            => content.Span.SequenceEqual(failFor)
                ? throw new InvalidOperationException("the asset store exploded")
                : inner.PutAsync(content, contentType, ct);

        public Task<Stream?> OpenAsync(string hash, CancellationToken ct) => inner.OpenAsync(hash, ct);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
