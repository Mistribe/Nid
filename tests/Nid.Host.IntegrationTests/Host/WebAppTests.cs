using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>Single-image design §3: the host serves the SPA from a web root, as nid-web did.</summary>
public class WebAppTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Spa = "<html>spa</html>";

    /// <summary>A web root with an index and one asset, a headers file, and a file next to the root that must never be served.</summary>
    private sealed class WebFolder
    {
        private WebFolder(string baseFolder, string root) { BaseFolder = baseFolder; Root = root; }

        public string BaseFolder { get; }
        public string Root { get; }
        public string HeadersFile => Path.Combine(BaseFolder, "security-headers.json");

        public static WebFolder Create(string headersJson = """{ "X-Test-Header": "1", "Content-Security-Policy": "default-src 'none'" }""")
        {
            var baseFolder = Directory.CreateTempSubdirectory("nid-web-").FullName;
            var root = Path.Combine(baseFolder, "wwwroot");
            Directory.CreateDirectory(Path.Combine(root, "assets"));
            File.WriteAllText(Path.Combine(root, "index.html"), Spa);
            File.WriteAllText(Path.Combine(root, "assets", "app.js"), "console.log(1)");
            File.WriteAllText(Path.Combine(baseFolder, "outside.txt"), "secret");
            File.WriteAllText(Path.Combine(baseFolder, "security-headers.json"), headersJson);
            return new WebFolder(baseFolder, root);
        }

        public Dictionary<string, string> Settings => new()
        {
            ["Web:Root"] = Root,
            ["Web:SecurityHeaders"] = HeadersFile,
        };
    }

    private Task<NidApp> StartAsync(WebFolder web) => NidApp.StartAsync(fixture, settings: web.Settings);

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(", ", values)
            : null;

    [Fact]
    public async Task A_deep_link_is_the_spa_revalidated_with_the_security_headers()
    {
        await using var app = await StartAsync(WebFolder.Create());

        foreach (var path in new[] { "/investments/abc", "/" })
        {
            var response = await app.RawAnonymousAsync(HttpMethod.Get, path);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(Spa, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("no-cache", Header(response, "Cache-Control"));
            Assert.Equal("1", Header(response, "X-Test-Header"));
            Assert.Equal("default-src 'none'", Header(response, "Content-Security-Policy"));
        }

        var head = await app.RawAnonymousAsync(HttpMethod.Head, "/investments/abc");
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_asset_is_immutable_with_the_security_headers()
    {
        await using var app = await StartAsync(WebFolder.Create());

        var response = await app.RawAnonymousAsync(HttpMethod.Get, "/assets/app.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("console.log(1)", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("public, max-age=31536000, immutable", Header(response, "Cache-Control"));
        Assert.Equal("1", Header(response, "X-Test-Header"));
    }

    [Theory]
    [InlineData("/assets/missing.js")]
    [InlineData("/Assets/missing.js")]
    public async Task A_missing_asset_is_a_404_never_the_spa(string path)
    {
        await using var app = await StartAsync(WebFolder.Create());

        var response = await app.RawAnonymousAsync(HttpMethod.Get, path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("spa", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("/mcp")]
    [InlineData("/api/v1/nope")]
    [InlineData("/auth/nope")]
    [InlineData("/health/nope")]
    public async Task Server_paths_are_never_the_spa_and_carry_no_spa_headers(string path)
    {
        await using var app = await StartAsync(WebFolder.Create());

        var response = await app.RawAnonymousAsync(HttpMethod.Get, path);

        Assert.DoesNotContain("spa", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Null(Header(response, "X-Test-Header"));
    }

    [Fact]
    public async Task A_prefix_is_not_a_segment()
    {
        await using var app = await StartAsync(WebFolder.Create());

        var response = await app.RawAnonymousAsync(HttpMethod.Get, "/apiary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Spa, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_post_to_a_deep_link_is_not_the_spa()
    {
        await using var app = await StartAsync(WebFolder.Create());

        var response = await app.RawAnonymousAsync(HttpMethod.Post, "/investments/abc");

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, response.StatusCode.ToString());
        Assert.DoesNotContain("spa", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_malformed_headers_file_stops_the_host_at_startup()
    {
        var web = WebFolder.Create("{not json");

        // The factory builds the host on the first request, which is when Program.cs runs.
        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var app = await StartAsync(web);
            await app.RawAnonymousAsync(HttpMethod.Get, "/");
        });

        var messages = new List<string>();
        for (var e = failure; e is not null; e = e.InnerException) messages.Add(e.Message);
        Assert.Contains(messages, m => m.Contains("security-headers.json"));
    }

    [Theory]
    [InlineData("/appsettings.json")]
    [InlineData("/assets/%2e%2e/%2e%2e/outside.txt")]
    [InlineData("/..%2foutside.txt")]
    public async Task Files_outside_the_web_root_are_not_served(string path)
    {
        await using var app = await StartAsync(WebFolder.Create());

        var response = await app.RawAnonymousAsync(HttpMethod.Get, path);

        Assert.DoesNotContain("secret", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound or HttpStatusCode.BadRequest, response.StatusCode.ToString());
        if (response.StatusCode == HttpStatusCode.OK)
            Assert.Equal(Spa, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Without_an_index_nothing_is_mounted()
    {
        var empty = Directory.CreateTempSubdirectory("nid-web-empty-").FullName;
        await using var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string> { ["Web:Root"] = empty });

        var response = await app.RawAnonymousAsync(HttpMethod.Get, "/investments/abc");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/assets/app.js", true)]
    [InlineData("/investments/x", true)]
    [InlineData("/api/v1/core/persons", true)]
    [InlineData("/auth/session", false)]
    [InlineData("/Auth/session", false)]
    [InlineData("/mcp", false)]
    [InlineData("/health/ready", false)]
    [InlineData("/api/v1/me/tokens", false)]
    [InlineData("/api/v1/core/me/tokens", false)]
    [InlineData("/api/v1/core/me/tokens/abc", false)]
    [InlineData("/API/v1/core/me/tokens", false)]
    public void Only_what_carries_no_secret_is_compressible(string path, bool compressible) =>
        Assert.Equal(compressible, Nid.Host.Web.WebAppSetup.IsCompressible(new Microsoft.AspNetCore.Http.PathString(path)));

    [Fact]
    public async Task Compressible_responses_are_compressed_and_token_responses_are_not()
    {
        await using var app = await StartAsync(WebFolder.Create());
        var accept = new Dictionary<string, string> { ["Accept-Encoding"] = "br" };

        var asset = await app.GetWithHeadersAsync(app.AdultA, "/assets/app.js", new Dictionary<string, string> { ["Accept-Encoding"] = "gzip" });
        var persons = await app.GetWithHeadersAsync(app.AdultA, "/api/v1/core/persons", accept);
        var tokens = await app.GetWithHeadersAsync(app.AdultA, "/api/v1/core/me/tokens", accept);

        Assert.Equal("gzip", Header(asset, "Content-Encoding"));
        Assert.Equal("br", Header(persons, "Content-Encoding"));
        Assert.Null(Header(tokens, "Content-Encoding"));
    }

    [Fact]
    public async Task Compression_applies_behind_an_https_proxy()
    {
        await using var app = await StartAsync(WebFolder.Create());

        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.ResponseCompression.ResponseCompressionOptions>>().Value;

        Assert.True(options.EnableForHttps);
    }

    [Fact]
    public async Task A_wrong_method_on_an_api_route_is_the_routings_405_not_the_fallbacks_404()
    {
        await using var app = await StartAsync(WebFolder.Create());

        var response = await app.RawAsync(app.AdultA, HttpMethod.Delete, "/api/v1/core/persons");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task The_stateless_mcp_endpoint_answers_405_to_get_and_delete_as_without_the_spa(string method)
    {
        await using var without = await NidApp.StartAsync(fixture);
        await using var with = await StartAsync(WebFolder.Create());

        var expected = await without.RawAnonymousAsync(new HttpMethod(method), "/mcp");
        var actual = await with.RawAnonymousAsync(new HttpMethod(method), "/mcp");

        Assert.Equal(expected.StatusCode, actual.StatusCode);
    }
}
