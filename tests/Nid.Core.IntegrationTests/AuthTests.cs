using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Nid.Core.Contracts;
using Nid.Host.Auth;
using Nid.TestSupport;

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

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.NewConnectionStringAsync();
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:nid", connectionString);
            builder.UseSetting("Auth:PublicUrl", "https://nid.example.test");
            builder.UseSetting("Auth:Authority", "https://auth.example.test");
            builder.UseSetting("Auth:ClientSecret", "test-secret");
            builder.UseSetting("Auth:CsrfKey", "0123456789abcdef0123456789abcdef");
        });
    }

    public async Task DisposeAsync() => await factory.DisposeAsync();

    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Session_endpoint_reports_not_authenticated_without_redirecting()
    {
        var response = await Client().GetAsync("/auth/session");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("authenticated").GetBoolean());
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

    [Fact]
    public async Task The_test_actor_header_is_ignored_outside_the_integration_environment()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/core/persons");
        request.Headers.Add(IntegrationTestGate.ActorHeader, Guid.NewGuid().ToString());

        var response = await Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_test_actor_header_does_not_bypass_csrf_outside_the_integration_environment()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/core/persons/dependents");
        request.Headers.Add(IntegrationTestGate.ActorHeader, Guid.NewGuid().ToString());

        var response = await Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
