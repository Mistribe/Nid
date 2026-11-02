using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>1F1 design §2.3: what the reference compose needs from the host, in an environment that is not Development.</summary>
public class HostingTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_answers_outside_development_with_status_only(string path)
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAnonymousAsync(HttpMethod.Get, path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Readiness_fails_without_the_database()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string>
        {
            // A reachable host that is not Postgres: the check fails fast instead of timing out.
            ["ConnectionStrings:nid"] = "Host=127.0.0.1;Port=1;Timeout=2;Database=none;Username=none;Password=none",
            ["Health:SkipMigrations"] = "true",
        });

        var response = await app.RawAnonymousAsync(HttpMethod.Get, "/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Data_protection_keys_are_written_to_the_configured_folder()
    {
        var keys = Directory.CreateTempSubdirectory("nid-keys-").FullName;
        await using var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string> { ["DataProtection:KeysPath"] = keys });

        // Protecting anything creates the first key, written where the setting says.
        using (var scope = app.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()
                .CreateProtector("test").Protect("x");

        Assert.NotEmpty(Directory.GetFiles(keys, "key-*.xml"));
    }

    [Fact]
    public async Task A_setting_is_read_from_a_secret_file()
    {
        var secrets = Directory.CreateTempSubdirectory("nid-secrets-").FullName;
        await File.WriteAllTextAsync(Path.Combine(secrets, "Catalog__Commit"), "from-a-secret-file", TestContext.Current.CancellationToken);
        await using var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string> { ["Secrets:Path"] = secrets });

        var catalog = app.Services.GetRequiredService<Nid.Subscriptions.Contracts.CatalogOptions>();

        Assert.Equal("from-a-secret-file", catalog.Commit);
    }

    [Fact]
    public async Task Forwarded_headers_are_trusted_from_the_configured_networks_only()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string> { ["Auth:TrustedNetworks:0"] = "10.99.0.0/16" });

        var options = app.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Equal(["10.99.0.0/16"], options.KnownIPNetworks.Select(n => n.ToString()));
        Assert.Empty(options.KnownProxies);
    }

    [Fact]
    public async Task With_no_trusted_network_the_private_and_loopback_ranges_are_trusted()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var options = app.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Equal(
            ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8", "fd00::/8", "::1/128"],
            options.KnownIPNetworks.Select(n => n.ToString()));
        Assert.Empty(options.KnownProxies);
    }

    [Fact]
    public async Task Trusting_any_network_is_an_explicit_choice()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string>
        {
            ["Auth:TrustedNetworks:0"] = "0.0.0.0/0",
            ["Auth:TrustedNetworks:1"] = "::/0",
        });

        var options = app.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Equal(["0.0.0.0/0", "::/0"], options.KnownIPNetworks.Select(n => n.ToString()));
    }

    [Theory]
    [InlineData("10.1.2.3", "https")]
    [InlineData("203.0.113.9", "http")]
    public async Task A_forwarded_scheme_is_honoured_from_a_private_address_only(string remoteAddress, string expectedScheme)
    {
        await using var app = await NidApp.StartAsync(fixture);

        var context = await app.Server.SendAsync(ctx =>
        {
            ctx.Request.Method = "GET";
            ctx.Request.Path = "/health/live";
            ctx.Request.Headers["X-Forwarded-Proto"] = "https";
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(expectedScheme, context.Request.Scheme);
    }
}
