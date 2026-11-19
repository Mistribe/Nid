using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Mcp;

/// <summary>#309: the Claude Desktop extension, zipped for this instance, to a signed-in adult only, never with a secret.</summary>
public class McpBundleTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly string Sources = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Hosting", "Nid.Host", "mcpb");

    /// <summary>The real template and package, with a stand-in for the bridge the image vendors.</summary>
    private static string BundleRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "nid-mcpb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "mcp-remote", "dist"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules", ".bin"));
        foreach (var file in new[] { "manifest.json", "package.json", "package-lock.json" })
            File.Copy(Path.Combine(Sources, file), Path.Combine(root, file));
        File.WriteAllText(Path.Combine(root, "node_modules", "mcp-remote", "dist", "proxy.js"), "// stand-in");
        File.WriteAllText(Path.Combine(root, "node_modules", ".bin", "mcp-remote"), "link");
        return root;
    }

    [Fact]
    public async Task An_adult_downloads_the_extension_filled_in_for_this_instance_without_a_secret()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string> { ["Mcp:BundleRoot"] = BundleRoot() });

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/mcp/nid.mcpb");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            ["manifest.json", "node_modules/mcp-remote/dist/proxy.js", "package.json"],
            zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));

        using var manifest = await JsonDocument.ParseAsync(zip.GetEntry("manifest.json")!.Open(), cancellationToken: TestContext.Current.CancellationToken);
        var root = manifest.RootElement;
        Assert.Equal("0.3", root.GetProperty("manifest_version").GetString());
        Assert.Equal("https://test.localhost/mcp", root.GetProperty("user_config").GetProperty("url").GetProperty("default").GetString());
        Assert.Equal("https://test.localhost/", root.GetProperty("privacy_policies")[0].GetString());
        var token = root.GetProperty("user_config").GetProperty("token");
        Assert.True(token.GetProperty("sensitive").GetBoolean());
        Assert.False(token.TryGetProperty("default", out _));
        // The token reaches the bridge through its environment, never its arguments: mcp-remote expands ${NID_AUTH} itself.
        var config = root.GetProperty("server").GetProperty("mcp_config");
        Assert.Equal("Bearer ${user_config.token}", config.GetProperty("env").GetProperty("NID_AUTH").GetString());
        Assert.Contains("Authorization:${NID_AUTH}", config.GetProperty("args").GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("user_config.token", config.GetProperty("args").GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("nid_pat_", root.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nobody_signed_in_gets_it_not_even_with_a_token()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string> { ["Mcp:BundleRoot"] = BundleRoot() });

        var anonymous = await app.RawAnonymousAsync(HttpMethod.Get, "/api/v1/mcp/nid.mcpb");
        var token = await app.BearerAsync((await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Read)).Secret, HttpMethod.Get, "/api/v1/mcp/nid.mcpb");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, token.StatusCode);
    }

    [Fact]
    public async Task A_server_without_the_built_extension_says_so()
    {
        var empty = Path.Combine(Path.GetTempPath(), "nid-mcpb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        await using var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string> { ["Mcp:BundleRoot"] = empty });

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/mcp/nid.mcpb");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
