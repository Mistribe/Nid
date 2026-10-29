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
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
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
        Assert.Equal("Unhealthy", await response.Content.ReadAsStringAsync());
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
        await File.WriteAllTextAsync(Path.Combine(secrets, "Catalog__Commit"), "from-a-secret-file");
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
    public async Task With_no_trusted_network_any_proxy_is_trusted_as_before()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var options = app.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Empty(options.KnownIPNetworks);
        Assert.Empty(options.KnownProxies);
    }
}
