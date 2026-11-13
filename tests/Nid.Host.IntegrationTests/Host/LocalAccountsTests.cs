using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nid.Core.Infrastructure.Persistence;
using Nid.Host.Auth;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using Npgsql;
using NodaTime;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>
/// Issue 215, local-accounts.md §18.1: the setup of a fresh instance, the password sign-in and its login CSRF, the lock,
/// the one-time links, the password rules, the transport, and no secret in the audit. A real host, cookies kept by hand
/// as a browser keeps them.
/// </summary>
public class LocalAccountsTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string PublicUrl = "https://nid.example.test";
    private const string AlicePassword = "a long walk to the lighthouse";
    private const string BobPassword = "seven green parrots at noon";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_fresh_instance_is_set_up_once_with_its_token_then_its_administrator_signs_in()
    {
        await using var host = await HostAsync();
        var setup = host.Factory.Services.GetRequiredService<SetupToken>();
        Assert.True(setup.IsOpen, "The start opened the setup of an empty household.");
        var token = setup.Open();
        var browser = host.Browser();

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("/auth/setup",
            new { token = "nid_setup_wrong", name = "Alice", login = "alice", password = AlicePassword })).StatusCode);
        var created = await browser.PostAsync("/auth/setup", new { token, name = "Alice", login = "Alice", password = AlicePassword });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("alice", (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("login").GetString());

        // Closed: the same token, or any other, is answered as if there were no setup.
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostAsync("/auth/setup",
            new { token, name = "Mallory", login = "mallory", password = AlicePassword })).StatusCode);
        Assert.False(setup.IsOpen);

        // The setup opens no session: the administrator signs in like anyone.
        Assert.False((await browser.SessionAsync()).GetProperty("authenticated").GetBoolean());
        var login = await browser.PostAsync("/auth/login/password", new { login = "ALICE", password = AlicePassword, returnUrl = "/dashboard" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal("/dashboard", (await login.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("returnUrl").GetString());
        var session = await browser.SessionAsync();
        Assert.True(session.GetProperty("authenticated").GetBoolean());
        Assert.Equal("Alice", session.GetProperty("person").GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/admin/system")).StatusCode);

        Assert.Single(await host.EventsAsync(SecurityEventNames.SetupCompleted));
        Assert.Contains(await host.EventsAsync(SecurityEventNames.Login),
            e => JsonSerializer.Deserialize<Dictionary<string, string>>(e.Data)!.GetValueOrDefault("method") == "password");
    }

    [Fact]
    public async Task Two_setups_at_once_create_one_administrator()
    {
        await using var host = await HostAsync();
        var token = host.Factory.Services.GetRequiredService<SetupToken>().Open();

        var answers = await Task.WhenAll(Enumerable.Range(0, 2).Select(i =>
            host.Browser().PostAsync("/auth/setup", new { token, name = $"Admin {i}", login = $"admin{i}", password = AlicePassword })));

        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.Created);
        await using var core = PostgresFixture.NewCoreContext(host.ConnectionString);
        Assert.Equal(1, await core.Persons.CountAsync(p => p.Kind == "adult", Ct));
    }

    [Fact]
    public async Task The_sign_in_routes_refuse_a_request_without_the_pre_session_cookie_its_token_its_origin_or_a_json_body()
    {
        await using var host = await HostAsync();
        var browser = host.Browser();
        await browser.SessionAsync();
        var body = new { login = "alice", password = AlicePassword };

        foreach (var path in LoginCsrf.Paths)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostAsync(path, body, csrf: false)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostAsync(path, body, origin: "https://evil.example")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostAsync(path, body, origin: null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostAsync(path, body, form: true)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await host.Browser().PostAsync(path, body, csrfOf: browser)).StatusCode);
        }
    }

    [Fact]
    public async Task An_unknown_identifier_and_a_wrong_password_get_the_same_answer()
    {
        await using var host = await HostAsync();
        await host.SetUpAliceAsync();

        var unknown = await host.Browser().PostAsync("/auth/login/password", new { login = "nobody", password = AlicePassword });
        var wrong = await host.Browser().PostAsync("/auth/login/password", new { login = "alice", password = "not the right one at all" });

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(unknown.StatusCode, wrong.StatusCode);
        var (a, b) = (await unknown.Content.ReadFromJsonAsync<JsonElement>(Ct), await wrong.Content.ReadFromJsonAsync<JsonElement>(Ct));
        Assert.Equal(a.GetProperty("title").GetString(), b.GetProperty("title").GetString());
        Assert.Equal("auth.invalid_credentials", b.GetProperty("code").GetString());
        var failed = await host.EventsAsync(SecurityEventNames.LoginFailed);
        Assert.Contains(failed, e => e.Data.Contains("unknown_login"));
        Assert.DoesNotContain(failed, e => e.Data.Contains("nobody"));
    }

    [Fact]
    public async Task Five_failures_lock_the_account_even_against_the_right_password_until_an_admin_unlocks_it()
    {
        await using var host = await HostAsync();
        var alice = await host.SetUpAliceAsync();
        var bob = await host.EnrolAsync(alice, "Bob", "bob", BobPassword);

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await host.Browser().PostAsync("/auth/login/password", new { login = "bob", password = $"wrong guess number {i}" })).StatusCode);

        var refused = await host.Browser().PostAsync("/auth/login/password", new { login = "bob", password = BobPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("auth.invalid_credentials", (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString());
        var locked = Assert.Single(await host.EventsAsync(SecurityEventNames.LoginLocked));
        Assert.Equal("1", JsonSerializer.Deserialize<Dictionary<string, string>>(locked.Data)!["level"]);

        var users = await (await alice.GetAsync("/api/v1/admin/users")).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.NotEqual(JsonValueKind.Null, users.EnumerateArray().Single(u => u.GetProperty("displayName").GetString() == "Bob").GetProperty("lockedUntil").ValueKind);

        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsync($"/api/v1/admin/users/{bob}/unlock", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Browser().PostAsync("/auth/login/password", new { login = "bob", password = BobPassword })).StatusCode);
        Assert.Single(await host.EventsAsync(SecurityEventNames.AccountUnlocked));
    }

    [Fact]
    public async Task Twenty_failures_from_one_address_are_answered_429()
    {
        await using var host = await HostAsync();
        for (var i = 0; i < 20; i++)
            await host.Browser().PostAsync("/auth/login/password", new { login = $"nobody{i}", password = AlicePassword });

        Assert.Equal(HttpStatusCode.TooManyRequests, (await host.Browser().PostAsync("/auth/login/password", new { login = "x", password = AlicePassword })).StatusCode);
    }

    [Fact]
    public async Task An_enrolment_link_works_once_and_makes_the_adult_active()
    {
        await using var host = await HostAsync();
        var alice = await host.SetUpAliceAsync();
        var bob = await alice.PreRegisterAsync("Bob", "bob@example.test");
        var link = await alice.PasswordLinkAsync(bob);
        Assert.Equal("enroll", link.GetProperty("purpose").GetString());
        var url = link.GetProperty("url").GetString()!;
        Assert.StartsWith(PublicUrl + "/password#nid_link_", url);
        var token = url[(url.IndexOf('#') + 1)..];

        var visitor = host.Browser();
        var inspected = await (await visitor.PostAsync("/auth/link/inspect", new { token })).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("enroll", inspected.GetProperty("purpose").GetString());
        Assert.Equal("Bob", inspected.GetProperty("name").GetString());
        Assert.Equal("bob@example.test", inspected.GetProperty("login").GetString());

        Assert.Equal(HttpStatusCode.OK, (await visitor.PostAsync("/auth/link/complete", new { token, login = "bob", password = BobPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.PostAsync("/auth/link/complete", new { token, login = "bob2", password = BobPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.PostAsync("/auth/link/inspect", new { token })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await visitor.PostAsync("/auth/login/password", new { login = "bob", password = BobPassword })).StatusCode);
        var users = await (await alice.GetAsync("/api/v1/admin/users")).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var row = users.EnumerateArray().Single(u => u.GetProperty("displayName").GetString() == "Bob");
        Assert.Equal("active", row.GetProperty("status").GetString());
        Assert.Equal(["password"], row.GetProperty("methods").EnumerateArray().Select(m => m.GetString()).ToArray());
        Assert.Single(await host.EventsAsync(SecurityEventNames.AccountEnrolled));
        Assert.Single(await host.EventsAsync(SecurityEventNames.CredentialLinkIssued));
    }

    [Fact]
    public async Task A_new_link_revokes_the_previous_one_and_an_identifier_taken_is_refused()
    {
        await using var host = await HostAsync();
        var alice = await host.SetUpAliceAsync();
        var bob = await alice.PreRegisterAsync("Bob", "bob@example.test");
        var first = Token(await alice.PasswordLinkAsync(bob));
        var second = Token(await alice.PasswordLinkAsync(bob));

        var visitor = host.Browser();
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.PostAsync("/auth/link/inspect", new { token = first })).StatusCode);
        var taken = await visitor.PostAsync("/auth/link/complete", new { token = second, login = "alice", password = BobPassword });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await visitor.PostAsync("/auth/link/complete", new { token = second, login = "bob", password = BobPassword })).StatusCode);

        // Revoked by the admin: unusable.
        var third = Token(await alice.PasswordLinkAsync(bob));
        Assert.Equal(HttpStatusCode.OK, (await alice.DeleteAsync($"/api/v1/admin/users/{bob}/password-link")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.PostAsync("/auth/link/inspect", new { token = third })).StatusCode);
    }

    [Fact]
    public async Task A_reset_link_replaces_the_password_and_closes_the_adult_s_sessions()
    {
        await using var host = await HostAsync();
        var alice = await host.SetUpAliceAsync();
        var bobId = await host.EnrolAsync(alice, "Bob", "bob", BobPassword);
        var bob = host.Browser();
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsync("/auth/login/password", new { login = "bob", password = BobPassword })).StatusCode);
        Assert.True((await bob.SessionAsync()).GetProperty("authenticated").GetBoolean());

        var link = await alice.PasswordLinkAsync(bobId);
        Assert.Equal("reset", link.GetProperty("purpose").GetString());
        var visitor = host.Browser();
        var token = Token(link);
        Assert.Equal("bob", (await (await visitor.PostAsync("/auth/link/inspect", new { token })).Content.ReadFromJsonAsync<JsonElement>(Ct))
            .GetProperty("login").GetString());
        const string fresh = "a brand new sentence for bob";
        Assert.Equal(HttpStatusCode.OK, (await visitor.PostAsync("/auth/link/complete", new { token, password = fresh })).StatusCode);

        Assert.False((await bob.SessionAsync()).GetProperty("authenticated").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Browser().PostAsync("/auth/login/password", new { login = "bob", password = BobPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Browser().PostAsync("/auth/login/password", new { login = "bob", password = fresh })).StatusCode);
        Assert.Single(await host.EventsAsync(SecurityEventNames.PasswordReset));
    }

    [Theory]
    [InlineData("eleven char", "auth.password_too_short")]
    [InlineData("password1234", "auth.password_too_common")]
    [InlineData("aaaaaaaaaaaaaaa", "auth.password_too_common")]
    [InlineData("qwertyuiop123", "auth.password_too_common")]
    [InlineData("i am bobby tables, really", "auth.password_too_common")]
    public async Task A_password_too_short_or_too_common_is_refused(string password, string code)
    {
        await using var host = await HostAsync();
        var alice = await host.SetUpAliceAsync();
        var bob = await alice.PreRegisterAsync("Bobby Tables", "bobby@example.test");
        var token = Token(await alice.PasswordLinkAsync(bob));

        var refused = await host.Browser().PostAsync("/auth/link/complete", new { token, login = "bob", password });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(code, (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Any_character_of_twelve_or_more_is_accepted()
    {
        await using var host = await HostAsync();
        var alice = await host.SetUpAliceAsync();
        var bob = await alice.PreRegisterAsync("Bob", "bob@example.test");
        var token = Token(await alice.PasswordLinkAsync(bob));
        const string password = "🦊 renard rusé ௐ 12";

        Assert.Equal(HttpStatusCode.OK, (await host.Browser().PostAsync("/auth/link/complete", new { token, login = "bob", password })).StatusCode);
        // NFKC: the same password typed with a full-width digit signs in.
        Assert.Equal(HttpStatusCode.OK, (await host.Browser().PostAsync("/auth/login/password", new { login = "bob", password = "🦊 renard rusé ௐ １2" })).StatusCode);
    }

    [Fact]
    public async Task A_hash_with_fewer_iterations_is_recomputed_at_sign_in()
    {
        await using var host = await HostAsync();
        await host.SetUpAliceAsync();
        var weak = new PasswordHasher<object>(Options.Create(new PasswordHasherOptions { IterationCount = 100_000 })).HashPassword(new object(), AlicePassword);
        await using (var core = PostgresFixture.NewCoreContext(host.ConnectionString))
            await core.LocalCredentials.ExecuteUpdateAsync(s => s.SetProperty(c => c.PasswordHash, weak), Ct);

        Assert.Equal(HttpStatusCode.OK, (await host.Browser().PostAsync("/auth/login/password", new { login = "alice", password = AlicePassword })).StatusCode);

        await using var after = PostgresFixture.NewCoreContext(host.ConnectionString);
        var stored = await after.LocalCredentials.Select(c => c.PasswordHash).SingleAsync(Ct);
        Assert.NotEqual(weak, stored);
        // Format V3: 0x01, the PRF (4 bytes), then the iteration count, big-endian.
        var bytes = Convert.FromBase64String(stored);
        Assert.Equal(LocalPasswordHasher.Iterations, (bytes[5] << 24) | (bytes[6] << 16) | (bytes[7] << 8) | bytes[8]);
    }

    [Fact]
    public async Task A_password_sent_over_plain_http_is_refused_unless_the_hoster_allowed_it()
    {
        await using var host = await HostAsync();
        var browser = host.Browser(baseAddress: "http://nid.example.test");

        var refused = await browser.PostAsync("/auth/login/password", new { login = "alice", password = AlicePassword });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("auth.https_required", (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task No_password_hash_or_link_reaches_the_audit()
    {
        await using var host = await HostAsync();
        var alice = await host.SetUpAliceAsync();
        var bob = await alice.PreRegisterAsync("Bob", "bob@example.test");
        var token = Token(await alice.PasswordLinkAsync(bob));
        await host.Browser().PostAsync("/auth/link/complete", new { token, login = "bob", password = BobPassword });
        await host.Browser().PostAsync("/auth/login/password", new { login = "bob", password = "a wrong one, of course" });
        await host.Browser().PostAsync("/auth/login/password", new { login = "bob", password = BobPassword });

        await using var core = PostgresFixture.NewCoreContext(host.ConnectionString);
        var hashes = await core.LocalCredentials.Select(c => c.PasswordHash).ToListAsync(Ct);
        await using var connection = new NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select coalesce(string_agg(t::text, '\n'), '') from platform.audit_log t", connection);
        var audit = (string)(await command.ExecuteScalarAsync(Ct))!;

        Assert.NotEmpty(audit);
        foreach (var secret in hashes.Append(token).Append(BobPassword).Append(AlicePassword).Append(LocalAccountStoreHash(token)))
            Assert.DoesNotContain(secret, audit);
    }

    [Fact]
    public async Task With_oidc_only_there_is_no_password_route()
    {
        await using var host = await HostAsync(new() { ["Auth:Mode"] = "oidc", ["Auth:Authority"] = "https://auth.example.test", ["Auth:ClientSecret"] = "s" });
        var browser = host.Browser();
        await browser.SessionAsync();

        Assert.Null(browser.PreCsrf);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostAsync("/auth/login/password", new { login = "a", password = "b" })).StatusCode);
        Assert.False(host.Factory.Services.GetRequiredService<SetupToken>().IsOpen);
    }

    private static string Token(JsonElement link)
    {
        var url = link.GetProperty("url").GetString()!;
        return url[(url.IndexOf('#') + 1)..];
    }

    private static string LocalAccountStoreHash(string token) => LocalAccountStore.Hash(token);

    private async Task<TestHost> HostAsync(Dictionary<string, string>? settings = null)
    {
        var connectionString = await postgres.NewConnectionStringAsync();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:nid", connectionString);
            builder.UseSetting("Auth:PublicUrl", PublicUrl);
            builder.UseSetting("Auth:CsrfKey", "0123456789abcdef0123456789abcdef");
            builder.UseSetting("Jobs:Enabled", "false");
            builder.UseSetting("Assets:Root", Path.Combine(Path.GetTempPath(), "nid-local-assets-" + Guid.NewGuid().ToString("N")));
            builder.UseSetting("Documents:Storage:Path", Path.Combine(Path.GetTempPath(), "nid-local-documents-" + Guid.NewGuid().ToString("N")));
            foreach (var (key, value) in settings ?? [])
                builder.UseSetting(key, value);
        });
        factory.CreateClient().Dispose();
        return new TestHost(factory, connectionString);
    }

    private sealed class TestHost(WebApplicationFactory<Program> factory, string connectionString) : IAsyncDisposable
    {
        public WebApplicationFactory<Program> Factory => factory;
        public string ConnectionString => connectionString;

        public Browser Browser(string baseAddress = PublicUrl) => new(factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, BaseAddress = new Uri(baseAddress), HandleCookies = false,
        }));

        /// <summary>The setup, then Alice signed in: the administrator's browser.</summary>
        public async Task<Browser> SetUpAliceAsync()
        {
            var token = factory.Services.GetRequiredService<SetupToken>().Open();
            var browser = Browser();
            var setup = await browser.PostAsync("/auth/setup", new { token, name = "Alice", login = "alice", password = AlicePassword });
            Assert.True(setup.StatusCode == HttpStatusCode.Created, await setup.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, (await browser.PostAsync("/auth/login/password", new { login = "alice", password = AlicePassword })).StatusCode);
            await browser.SessionAsync();
            return browser;
        }

        /// <summary>An adult pre-registered by <paramref name="admin"/>, enrolled by their link.</summary>
        public async Task<Guid> EnrolAsync(Browser admin, string name, string login, string password)
        {
            var id = await admin.PreRegisterAsync(name, $"{login}@example.test");
            var token = LocalAccountsTests.Token(await admin.PasswordLinkAsync(id));
            Assert.Equal(HttpStatusCode.OK, (await Browser().PostAsync("/auth/link/complete", new { token, login, password })).StatusCode);
            return id;
        }

        public async Task<List<Nid.Platform.Infrastructure.Audit.AuditEntryRow>> EventsAsync(string name)
        {
            await using var platform = PostgresFixture.NewPlatformContext(connectionString);
            return await platform.AuditLog.Where(r => r.Category == "security" && r.Action == name).OrderBy(r => r.Seq).ToListAsync();
        }

        public ValueTask DisposeAsync() => factory.DisposeAsync();
    }

    /// <summary>A browser: its cookies, its CSRF token (pre-session, then the session's), same-origin requests.</summary>
    private sealed class Browser(HttpClient client)
    {
        private readonly Dictionary<string, string> cookies = [];
        public string? PreCsrf { get; private set; }
        private string? sessionCsrf;

        public async Task<JsonElement> SessionAsync()
        {
            var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "/auth/session"));
            var session = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            var csrf = session.GetProperty("csrf");
            if (session.GetProperty("authenticated").GetBoolean()) sessionCsrf = csrf.GetString();
            else { PreCsrf = csrf.ValueKind == JsonValueKind.Null ? null : csrf.GetString(); sessionCsrf = null; }
            return session;
        }

        public async Task<HttpResponseMessage> PostAsync(
            string path, object body, bool csrf = true, string? origin = PublicUrl, bool form = false, Browser? csrfOf = null)
        {
            if (PreCsrf is null && sessionCsrf is null) await SessionAsync();
            var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = form
                    ? new FormUrlEncodedContent(new Dictionary<string, string> { ["body"] = JsonSerializer.Serialize(body) })
                    : JsonContent.Create(body),
            };
            if (origin is not null) request.Headers.Add("Origin", origin);
            var token = (csrfOf ?? this).Token(path);
            if (csrf && token is not null) request.Headers.Add("X-Nid-Csrf", token);
            var response = await SendAsync(request);
            if (path == "/auth/login/password" && response.IsSuccessStatusCode) await SessionAsync();
            return response;
        }

        public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(new HttpRequestMessage(HttpMethod.Get, path));

        public Task<HttpResponseMessage> DeleteAsync(string path)
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, path);
            request.Headers.Add("Origin", PublicUrl);
            if (sessionCsrf is not null) request.Headers.Add("X-Nid-Csrf", sessionCsrf);
            return SendAsync(request);
        }

        public async Task<Guid> PreRegisterAsync(string name, string email)
        {
            var response = await PostAsync("/api/v1/admin/users", new { displayName = name, email });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        }

        public async Task<JsonElement> PasswordLinkAsync(Guid person)
        {
            var response = await PostAsync($"/api/v1/admin/users/{person}/password-link", new { revokeTokens = false });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        }

        private string? Token(string path) => path.StartsWith("/api", StringComparison.Ordinal) ? sessionCsrf : PreCsrf;

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            if (cookies.Count > 0)
                request.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => $"{c.Key}={c.Value}")));
            var response = await client.SendAsync(request, Ct);
            if (response.Headers.TryGetValues("Set-Cookie", out var set))
                foreach (var cookie in set)
                {
                    var pair = cookie.Split(';')[0];
                    var at = pair.IndexOf('=');
                    var (name, value) = (pair[..at], pair[(at + 1)..]);
                    if (value.Length == 0 || cookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase)) cookies.Remove(name);
                    else cookies[name] = value;
                }
            return response;
        }
    }
}
