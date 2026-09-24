using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.Host.Auth;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Core.IntegrationTests;

public class ReturnUrlTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/subscriptions?tab=all")]
    public void Relative_paths_are_accepted(string url) => Assert.True(ReturnUrl.IsSafe(url));

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("evil")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("/ok\r\nSet-Cookie: x=1")]
    public void Anything_else_is_refused(string? url) => Assert.False(ReturnUrl.IsSafe(url));
}

public class CsrfTokenTests
{
    private static readonly PersonId Person = PersonId.New();
    private const string Key = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void Token_is_stable_for_the_same_session()
        => Assert.Equal(SessionClaims.CsrfToken(Key, Person, 3), SessionClaims.CsrfToken(Key, Person, 3));

    [Fact]
    public void Signing_out_everywhere_changes_the_token()
        => Assert.NotEqual(SessionClaims.CsrfToken(Key, Person, 3), SessionClaims.CsrfToken(Key, Person, 4));

    [Fact]
    public void Token_depends_on_the_person_and_the_key()
    {
        Assert.NotEqual(SessionClaims.CsrfToken(Key, Person, 1), SessionClaims.CsrfToken(Key, PersonId.New(), 1));
        Assert.NotEqual(SessionClaims.CsrfToken(Key, Person, 1), SessionClaims.CsrfToken(Key + "x", Person, 1));
    }
}

[Collection("postgres")]
public class UnauthenticatedHttpTests(PostgresFixture postgres) : IAsyncLifetime
{
    private WebApplicationFactory<Program> factory = null!;
    private string connectionString = null!;

    public async Task InitializeAsync()
    {
        connectionString = await postgres.NewConnectionStringAsync();
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:nid", connectionString);
            builder.UseSetting("Auth:PublicUrl", "https://nid.example.test");
            builder.UseSetting("Auth:Authority", "https://auth.example.test");
            builder.UseSetting("Auth:ClientSecret", "test-secret");
            builder.UseSetting("Auth:CsrfKey", "0123456789abcdef0123456789abcdef");
            // Jobs on by default would start Quartz and, on this empty rate table, a backfill against the real sources.
            builder.UseSetting("Jobs:Enabled", "false");
        });
    }

    public async Task DisposeAsync() => await factory.DisposeAsync();

    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>
    /// A GUID that names nobody is refused whether or not the IntegrationTest bypass is open — the guard
    /// tests below would pass either way with one. Seeding a real person (as <c>NidApp</c> itself does) and
    /// sending *their* id is what makes the assertion actually exercise the environment gate: if it were
    /// open here, this identity would authenticate with every scope (<c>Actor.Browser</c>) and a real,
    /// person-and-epoch-bound CSRF token would even be obtainable from <c>/auth/session</c>.
    /// </summary>
    private async Task<Guid> SeedPersonAsync()
    {
        await using var db = PostgresFixture.NewCoreContext(connectionString);
        var id = Guid.CreateVersion7();
        db.Persons.Add(new PersonRow
        {
            Id = id,
            Kind = "adult",
            OidcSubject = "guard-test-" + id.ToString("N")[..8],
            DisplayName = "guard-test-person",
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
        });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task Session_endpoint_reports_not_authenticated_without_redirecting()
    {
        var response = await Client().GetAsync("/auth/session");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("authenticated").GetBoolean());
        // Signing out of Nid leaves the identity provider's session open: the signed-out page links to it.
        Assert.Equal("https://auth.example.test", body.GetProperty("identityProvider").GetString());
    }

    [Fact]
    public async Task Api_answers_401_without_a_html_redirect()
    {
        var response = await Client().GetAsync("/api/v1/core/persons");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Login_refuses_an_absolute_return_url()
    {
        var response = await Client().GetAsync("/auth/login?returnUrl=https://evil.example/");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unsafe_requests_without_the_csrf_header_are_refused()
    {
        var response = await Client().PostAsync("/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Cross_site_requests_are_refused_even_with_a_header()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/core/persons/dependents");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        request.Headers.Add(CsrfMiddleware.HeaderName, "anything");
        var response = await Client().SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // This host never sets IntegrationTest (see InitializeAsync above): these two guard the boundary that
    // IntegrationTestAuthenticationHandler and NidApp rely on — that IntegrationTestGate.ActorHeader is inert
    // everywhere else, so a future change (moving the header read, or getting the environment string wrong
    // in one of the sites that names it) fails a test instead of silently widening a production bypass.
    // The header must name a person who actually exists: an unknown id 401s/403s regardless of whether the
    // gate is open or closed, which would prove nothing about the gate itself (see SeedPersonAsync above).

    [Fact]
    public async Task The_test_actor_header_is_ignored_outside_the_integration_environment()
    {
        var personId = await SeedPersonAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/core/persons");
        request.Headers.Add(IntegrationTestGate.ActorHeader, personId.ToString());

        var response = await Client().SendAsync(request);

        // Were the gate open, this real identity would authenticate with every scope and the list would
        // succeed (200). It must not — spelled out, not just the specific status code, so the intent reads.
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_test_actor_header_does_not_bypass_csrf_outside_the_integration_environment()
    {
        var personId = await SeedPersonAsync();

        // The attack this guards: were the gate open, the header alone would already grant a real session,
        // and /auth/session would hand back a real, usable CSRF token for it — exactly as NidApp itself
        // relies on. Attempt precisely that before writing, rather than sending no token at all: a POST
        // with no token is refused whether the gate is open or closed and would prove nothing.
        var sessionRequest = new HttpRequestMessage(HttpMethod.Get, "/auth/session");
        sessionRequest.Headers.Add(IntegrationTestGate.ActorHeader, personId.ToString());
        var session = await (await Client().SendAsync(sessionRequest)).Content.ReadFromJsonAsync<JsonElement>();
        var csrf = session.TryGetProperty("csrf", out var token) ? token.GetString() : "not-a-real-token";

        var writeRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/core/persons/dependents");
        writeRequest.Headers.Add(IntegrationTestGate.ActorHeader, personId.ToString());
        writeRequest.Headers.Add(CsrfMiddleware.HeaderName, csrf!);

        var response = await Client().SendAsync(writeRequest);

        // Were the gate open, /auth/session above would have handed back a real, usable token and this
        // write would succeed (201 Created). It must not.
        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
