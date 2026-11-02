using Microsoft.Extensions.DependencyInjection;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>1C2c design §3: the host binds <c>Assets:Root</c>, and every test host gets a folder of its own.</summary>
public class AssetStoreConfigurationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task The_host_stores_assets_under_Assets_Root()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var hash = await app.Services.GetRequiredService<IAssetStore>().PutAsync(new byte[] { 1, 2, 3 }, "image/webp", TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(app.AssetsRoot, hash[..2], hash + ".webp")));
        Assert.StartsWith(Path.GetTempPath(), app.AssetsRoot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_test_host_leaves_no_asset_behind()
    {
        string root;
        await using (var app = await NidApp.StartAsync(fixture))
        {
            root = app.AssetsRoot;
            await app.Services.GetRequiredService<IAssetStore>().PutAsync(new byte[] { 4 }, "image/webp", TestContext.Current.CancellationToken);
        }

        Assert.False(Directory.Exists(root));
    }
}
