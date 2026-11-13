using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Nid.Core.Infrastructure.Persistence;
using Nid.Host.Demo;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Host.IntegrationTests.Demo;

/// <summary>
/// Issue 203: demo mode signs in without an identity provider, loads the demo household into an empty database, refuses
/// a database holding anyone else, and refuses what would reach outside the demo. A real host (not the IntegrationTest
/// environment): what is tested is the cookie a browser gets.
/// </summary>
public class DemoModeTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string PublicUrl = "https://demo.example.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_empty_database_gets_the_demo_household_and_a_visitor_signs_in_as_alice_in_one_click()
    {
        var connectionString = await postgres.NewConnectionStringAsync();
        await using var factory = DemoFactory(connectionString);
        using var client = Client(factory);

        var login = await client.GetAsync("/auth/login?returnUrl=%2Fdashboard", Ct);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/demo?returnUrl=%2Fdashboard", login.Headers.Location!.OriginalString);

        var cookie = await SignInAsync(client, "alice", "/dashboard");
        var session = await SessionAsync(client, cookie);
        Assert.True(session.GetProperty("authenticated").GetBoolean());
        Assert.True(session.GetProperty("demo").GetBoolean());
        Assert.Equal("Alice", session.GetProperty("person").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("identityProvider").ValueKind);

        var persons = await GetJsonAsync(client, cookie, "/api/v1/core/persons");
        Assert.Equal(["Alice", "Bob", "Léa"], persons.EnumerateArray().Select(p => p.GetProperty("displayName").GetString()).Order().ToArray());
        var envelopes = await GetJsonAsync(client, cookie, "/api/v1/investments/envelopes?scope=household");
        Assert.True(envelopes.GetProperty("items").GetArrayLength() > 0, "The demo household has investment envelopes.");
    }

    [Fact]
    public async Task A_restart_on_the_same_database_keeps_the_demo_household_as_it_is()
    {
        var connectionString = await postgres.NewConnectionStringAsync();
        await using (var first = DemoFactory(connectionString))
            Client(first).Dispose();
        // A visitor signed in as Alice pre-registered an adult: still demo data, the demo must start again.
        await using (var db = PostgresFixture.NewCoreContext(connectionString))
        {
            db.Persons.Add(new PersonRow
            {
                Id = Guid.CreateVersion7(), Kind = "adult", OidcSubject = null, DisplayName = "Invited", Email = "invited@example.test",
                CreatedAt = SystemClock.Instance.GetCurrentInstant(),
            });
            await db.SaveChangesAsync(Ct);
        }

        await using var second = DemoFactory(connectionString);
        using var client = Client(second);
        var cookie = await SignInAsync(client, "bob", "/");
        var persons = await GetJsonAsync(client, cookie, "/api/v1/core/persons");
        Assert.Equal(4, persons.GetArrayLength());
        Assert.Equal(2, persons.EnumerateArray().Count(p => p.GetProperty("email").GetString() is "alice@example.test" or "bob@example.test"));
    }

    [Fact]
    public async Task Demo_mode_refuses_to_start_on_a_database_holding_another_household()
    {
        var connectionString = await postgres.NewConnectionStringAsync();
        await using (var db = PostgresFixture.NewCoreContext(connectionString))
        {
            db.Persons.Add(new PersonRow
            {
                Id = Guid.CreateVersion7(), Kind = "adult", OidcSubject = "real-adult", DisplayName = "Someone", Email = "someone@example.test",
                CreatedAt = SystemClock.Instance.GetCurrentInstant(),
            });
            await db.SaveChangesAsync(Ct);
        }

        await using var factory = DemoFactory(connectionString);
        var error = Assert.ThrowsAny<Exception>(() => Client(factory));
        Assert.Contains("outside the demo household", error.ToString());
    }

    [Fact]
    public async Task The_demo_creates_no_access_token_and_no_webhook()
    {
        var connectionString = await postgres.NewConnectionStringAsync();
        await using var factory = DemoFactory(connectionString);
        using var client = Client(factory);
        var cookie = await SignInAsync(client, "alice", "/");
        var csrf = (await SessionAsync(client, cookie)).GetProperty("csrf").GetString()!;

        foreach (var (method, path, body) in new[]
                 {
                     (HttpMethod.Post, "/api/v1/core/me/tokens", (object)new { name = "assistant", scopes = new[] { "read" } }),
                     (HttpMethod.Put, "/api/v1/platform/notifications/discord", new { url = "https://discord.com/api/webhooks/1/x" }),
                     (HttpMethod.Put, "/api/v1/admin/notifications/discord", new { url = "https://discord.com/api/webhooks/1/x" }),
                 })
        {
            using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
            request.Headers.Add("Cookie", cookie);
            request.Headers.Add("X-Nid-Csrf", csrf);
            var response = await client.SendAsync(request, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path} answered {response.StatusCode}");
        }
    }

    [Fact]
    public async Task An_unknown_demo_adult_or_an_absolute_return_url_is_refused()
    {
        var connectionString = await postgres.NewConnectionStringAsync();
        await using var factory = DemoFactory(connectionString);
        using var client = Client(factory);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/auth/demo/carol", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/auth/demo/alice?returnUrl=https%3A%2F%2Fevil.example", Ct)).StatusCode);
    }

    [Fact]
    public async Task Outside_demo_mode_there_is_no_demo_sign_in_and_login_goes_to_the_identity_provider()
    {
        var connectionString = await postgres.NewConnectionStringAsync();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            Common(builder, connectionString);
            builder.UseSetting("Auth:Authority", "https://auth.example.test");
            builder.UseSetting("Auth:ClientSecret", "test-secret");
        });
        using var client = Client(factory);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/auth/demo/alice", Ct)).StatusCode);
        var login = await client.GetAsync("/auth/login", Ct);
        Assert.DoesNotContain("/demo", login.Headers.Location?.OriginalString ?? "");
        var session = await SessionAsync(client, null);
        Assert.False(session.GetProperty("demo").GetBoolean());
        await using var db = PostgresFixture.NewCoreContext(connectionString);
        Assert.Empty(db.Persons);
    }

    [Theory]
    [InlineData("2026-10-28", 1, "2026-11-28")]
    [InlineData("2026-10-31", 1, "2026-11-30")]
    [InlineData("2026-09-30", 1, "2026-10-31")]
    [InlineData("2026-01-31", 1, "2026-02-28")]
    [InlineData("2026-10-15T08:30:00Z", 3, "2027-01-15T08:30:00Z")]
    [InlineData("2026-03-15", -3, "2025-12-15")]
    [InlineData("demo-spotify", 4, "demo-spotify")]
    [InlineData("2026-13-01", 1, "2026-13-01")]
    public void A_date_moves_by_whole_months_as_generate_mjs_moves_it(string value, int months, string expected)
        => Assert.Equal(expected, DemoHousehold.ShiftDate(value, months));

    private static WebApplicationFactory<Program> DemoFactory(string connectionString)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            Common(builder, connectionString);
            builder.UseSetting("Nid:Demo:Enabled", "true");
        });

    private static void Common(IWebHostBuilder builder, string connectionString)
    {
        builder.UseSetting("ConnectionStrings:nid", connectionString);
        builder.UseSetting("Auth:PublicUrl", PublicUrl);
        builder.UseSetting("Auth:CsrfKey", "0123456789abcdef0123456789abcdef");
        builder.UseSetting("Jobs:Enabled", "false");
        builder.UseSetting("Assets:Root", Path.Combine(Path.GetTempPath(), "nid-demo-assets-" + Guid.NewGuid().ToString("N")));
        builder.UseSetting("Documents:Storage:Path", Path.Combine(Path.GetTempPath(), "nid-demo-documents-" + Guid.NewGuid().ToString("N")));
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri(PublicUrl), HandleCookies = false });

    /// <summary>The demo sign-in, and the session cookie it sets (sent back by hand: the client keeps no cookies).</summary>
    private static async Task<string> SignInAsync(HttpClient client, string key, string returnUrl)
    {
        var response = await client.GetAsync($"/auth/demo/{key}?returnUrl={Uri.EscapeDataString(returnUrl)}", Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(returnUrl, response.Headers.Location!.OriginalString);
        var setCookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-nid=", StringComparison.Ordinal));
        return setCookie.Split(';')[0];
    }

    private static async Task<JsonElement> SessionAsync(HttpClient client, string? cookie)
        => await GetJsonAsync(client, cookie, "/auth/session");

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string? cookie, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie is not null)
            request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"GET {path} answered {response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
