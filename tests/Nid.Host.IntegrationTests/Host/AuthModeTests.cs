using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Nid.Core.Infrastructure.Persistence;
using Nid.Host.Auth;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>
/// Issue 215, first step: <c>Auth:Mode</c> and its default, <c>Auth:AdminSource</c>, plain HTTP as an explicit opt-in, and
/// what <c>/auth/session</c> and the admin area say about them (local-accounts spec §2, §6, §12). A real host, as a browser
/// sees it: no IntegrationTest environment.
/// </summary>
public class AuthModeTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string PublicUrl = "https://nid.example.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null, "https://auth.example.test", null, AuthMode.Oidc, AdminSource.IdentityProvider)]
    [InlineData(null, null, null, AuthMode.Local, AdminSource.Nid)]
    [InlineData("", "", "", AuthMode.Local, AdminSource.Nid)]
    [InlineData("local", "https://auth.example.test", null, AuthMode.Local, AdminSource.Nid)]
    [InlineData("BOTH", "https://auth.example.test", null, AuthMode.Both, AdminSource.Nid)]
    [InlineData("both", "https://auth.example.test", "identity_provider", AuthMode.Both, AdminSource.IdentityProvider)]
    [InlineData("oidc", "https://auth.example.test", "nid", AuthMode.Oidc, AdminSource.Nid)]
    public void The_mode_defaults_to_oidc_with_an_authority_and_to_local_without(
        string? mode, string? authority, string? adminSource, AuthMode expectedMode, AdminSource expectedSource)
    {
        var settings = AuthSettings.Resolve(new NidAuthOptions
        {
            Mode = mode, Authority = authority ?? "", ClientSecret = authority is null ? "" : "secret", AdminSource = adminSource,
            PublicUrl = PublicUrl,
        });

        Assert.Equal(expectedMode, settings.Mode);
        Assert.Equal(expectedSource, settings.AdminSource);
        Assert.False(settings.AllowInsecureHttp);
    }

    [Theory]
    [InlineData("ldap", "https://auth.example.test", "secret", null, "https://nid.example.test", false, "Auth:Mode must be")]
    [InlineData("oidc", "", "", null, "https://nid.example.test", false, "set Auth:Authority and Auth:ClientSecret")]
    [InlineData("both", "https://auth.example.test", "", null, "https://nid.example.test", false, "set Auth:Authority and Auth:ClientSecret")]
    [InlineData("local", "", "", "identity_provider", "https://nid.example.test", false, "needs an identity provider")]
    [InlineData("local", "", "", "groups", "https://nid.example.test", false, "Auth:AdminSource must be")]
    [InlineData("local", "", "", null, "http://nid.example.test", false, "Auth:AllowInsecureHttp=true")]
    [InlineData("local", "", "", null, "http://192.168.1.20:8080", false, "Auth:AllowInsecureHttp=true")]
    public void A_configuration_that_cannot_work_stops_the_start_with_its_reason(
        string mode, string authority, string secret, string? adminSource, string publicUrl, bool insecure, string message)
    {
        var error = Assert.Throws<InvalidOperationException>(() => AuthSettings.Resolve(new NidAuthOptions
        {
            Mode = mode, Authority = authority, ClientSecret = secret, AdminSource = adminSource, PublicUrl = publicUrl,
            AllowInsecureHttp = insecure,
        }));
        Assert.Contains(message, error.Message);
    }

    [Theory]
    [InlineData("http://localhost:8080")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://[::1]:8080")]
    public void A_loopback_address_needs_no_opt_in_for_plain_http(string publicUrl)
        => Assert.Equal(AuthMode.Local, AuthSettings.Resolve(new NidAuthOptions { PublicUrl = publicUrl }).Mode);

    [Fact]
    public void Plain_http_on_another_address_is_accepted_once_the_hoster_allows_it()
        => Assert.True(AuthSettings.Resolve(new NidAuthOptions { PublicUrl = "http://nid.home.arpa", AllowInsecureHttp = true }).AllowInsecureHttp);

    [Fact]
    public async Task An_existing_oidc_installation_keeps_signing_in_through_the_identity_provider()
    {
        await using var factory = Factory(await postgres.NewConnectionStringAsync(), new()
        {
            ["Auth:Authority"] = "https://auth.example.test",
            ["Auth:ClientSecret"] = "test-secret",
        });
        using var client = Client(factory);

        var login = await client.GetAsync("/auth/login?returnUrl=%2Fdashboard", Ct);
        Assert.DoesNotContain(AuthEndpoints.LoginPagePath + "?", login.Headers.Location?.OriginalString ?? "");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/auth/login/oidc", Ct)).StatusCode);

        var session = await SessionAsync(client);
        Assert.Equal(["oidc"], Methods(session));
        Assert.False(session.GetProperty("setupRequired").GetBoolean());
        Assert.Equal("https://auth.example.test", session.GetProperty("identityProvider").GetString());
    }

    [Fact]
    public async Task Without_an_identity_provider_sign_in_goes_to_the_sign_in_page_and_a_fresh_instance_waits_for_its_setup()
    {
        var connectionString = await postgres.NewConnectionStringAsync();
        await using var factory = Factory(connectionString, []);
        using var client = Client(factory);

        var login = await client.GetAsync("/auth/login?returnUrl=%2Fdashboard", Ct);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/login?returnUrl=%2Fdashboard", login.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/auth/login?returnUrl=https%3A%2F%2Fevil.example", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/auth/login/oidc", Ct)).StatusCode);

        var session = await SessionAsync(client);
        Assert.Equal(["password"], Methods(session));
        Assert.True(session.GetProperty("setupRequired").GetBoolean());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("identityProvider").ValueKind);

        // Once an adult exists, the setup is closed.
        await using (var db = PostgresFixture.NewCoreContext(connectionString))
        {
            db.Persons.Add(new PersonRow
            {
                Id = Guid.CreateVersion7(), Kind = "adult", DisplayName = "Alice", Email = "alice@example.test",
                CreatedAt = SystemClock.Instance.GetCurrentInstant(),
            });
            await db.SaveChangesAsync(Ct);
        }
        Assert.False((await SessionAsync(client)).GetProperty("setupRequired").GetBoolean());
    }

    [Fact]
    public async Task Both_modes_offer_the_sign_in_page_and_the_identity_provider_on_its_own_route()
    {
        await using var factory = Factory(await postgres.NewConnectionStringAsync(), new()
        {
            ["Auth:Mode"] = "both",
            ["Auth:Authority"] = "https://auth.example.test",
            ["Auth:ClientSecret"] = "test-secret",
        });
        using var client = Client(factory);

        Assert.Equal("/login?returnUrl=%2F", (await client.GetAsync("/auth/login", Ct)).Headers.Location!.OriginalString);
        // The identity provider does not exist: the challenge ends on the « unavailable » page, not on a 404.
        var oidc = await client.GetAsync("/auth/login/oidc?returnUrl=%2Fdashboard", Ct);
        Assert.Equal(HttpStatusCode.Redirect, oidc.StatusCode);
        Assert.DoesNotContain(AuthEndpoints.LoginPagePath + "?", oidc.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/auth/login/oidc?returnUrl=%2F%2Fevil.example", Ct)).StatusCode);

        var session = await SessionAsync(client);
        Assert.Equal(["password", "oidc"], Methods(session));
        Assert.True(session.GetProperty("setupRequired").GetBoolean());
    }

    [Fact]
    public async Task A_plain_http_address_without_the_opt_in_stops_the_start()
    {
        await using var factory = Factory(await postgres.NewConnectionStringAsync(), new() { ["Auth:PublicUrl"] = "http://nid.example.test" });

        var error = Assert.ThrowsAny<Exception>(() => Client(factory));
        Assert.Contains("Auth:AllowInsecureHttp", error.ToString());
    }

    [Fact]
    public async Task With_plain_http_allowed_the_session_cookie_travels_without_https()
    {
        const string insecureUrl = "http://nid.home.arpa";
        await using var factory = Factory(await postgres.NewConnectionStringAsync(), new()
        {
            ["Auth:PublicUrl"] = insecureUrl,
            ["Auth:AllowInsecureHttp"] = "true",
            // Demo mode signs in without a password: what is tested here is the cookie it sets over plain HTTP.
            ["Nid:Demo:Enabled"] = "true",
        });
        using var client = Client(factory, insecureUrl);

        var response = await client.GetAsync("/auth/demo/alice?returnUrl=%2F", Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("nid=", StringComparison.Ordinal));
        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Without_the_opt_in_the_session_cookie_keeps_its_host_prefix_and_secure()
    {
        await using var factory = Factory(await postgres.NewConnectionStringAsync(), new() { ["Nid:Demo:Enabled"] = "true" });
        using var client = Client(factory);

        var response = await client.GetAsync("/auth/demo/alice?returnUrl=%2F", Ct);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-nid=", StringComparison.Ordinal));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_admin_area_shows_the_mode_the_admin_source_and_no_identity_provider_to_probe_in_local_mode()
    {
        await using var app = await NidApp.StartAsync(postgres, settings: new Dictionary<string, string>
        {
            ["Auth:Mode"] = "local",
            ["Auth:PublicUrl"] = "http://nid.home.arpa",
            ["Auth:AllowInsecureHttp"] = "true",
        });

        var status = await app.GetAsync<JsonElement>(app.AdultA, "/api/v1/admin/system");

        Assert.Equal("not_used", status.GetProperty("health").GetProperty("identityProvider").GetString());
        var auth = status.GetProperty("auth");
        Assert.Equal("local", auth.GetProperty("mode").GetString());
        Assert.Equal("nid", auth.GetProperty("adminSource").GetString());
        Assert.True(auth.GetProperty("insecureHttp").GetBoolean());
    }

    private static string?[] Methods(JsonElement session)
        => session.GetProperty("methods").EnumerateArray().Select(m => m.GetString()).ToArray();

    private static WebApplicationFactory<Program> Factory(string connectionString, Dictionary<string, string> settings)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:nid", connectionString);
            builder.UseSetting("Auth:PublicUrl", PublicUrl);
            builder.UseSetting("Auth:CsrfKey", "0123456789abcdef0123456789abcdef");
            builder.UseSetting("Jobs:Enabled", "false");
            builder.UseSetting("Assets:Root", Path.Combine(Path.GetTempPath(), "nid-auth-assets-" + Guid.NewGuid().ToString("N")));
            builder.UseSetting("Documents:Storage:Path", Path.Combine(Path.GetTempPath(), "nid-auth-documents-" + Guid.NewGuid().ToString("N")));
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
        });

    private static HttpClient Client(WebApplicationFactory<Program> factory, string baseAddress = PublicUrl)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri(baseAddress), HandleCookies = false });

    private static async Task<JsonElement> SessionAsync(HttpClient client)
    {
        var response = await client.GetAsync("/auth/session", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"GET /auth/session answered {response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
