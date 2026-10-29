using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Host.IntegrationTests.Admin;
using Nid.Host.IntegrationTests.Audit;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>Spec 4A §3, §4, §7: a bearer token on /api.</summary>
public class BearerTokenTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Capabilities = "/api/v1/platform/capabilities";

    [Fact]
    public async Task A_valid_token_reads_and_its_scopes_restrict()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var read = await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Read);
        var propose = await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Propose);
        var direct = await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Direct);
        var dependent = new { displayName = "Kid" };

        Assert.Equal(HttpStatusCode.OK, (await app.BearerAsync(read.Secret, HttpMethod.Get, Capabilities)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.BearerAsync(read.Secret, HttpMethod.Post, "/api/v1/core/persons/dependents", dependent)).StatusCode);
        // 4C1: « Lecture + propositions » proposes; only « Écriture directe » writes.
        Assert.Equal(HttpStatusCode.Forbidden, (await app.BearerAsync(propose.Secret, HttpMethod.Post, "/api/v1/core/persons/dependents", dependent)).StatusCode);
        // A bearer POST needs no CSRF header.
        Assert.True((await app.BearerAsync(direct.Secret, HttpMethod.Post, "/api/v1/core/persons/dependents", dependent)).IsSuccessStatusCode);
        Assert.NotNull((await TokenRowsAsync(app)).Single(t => t.Id == direct.Token.Id).LastUsedAt);
    }

    [Fact]
    public async Task A_token_never_reaches_the_admin_area_nor_the_browser_only_routes()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var direct = await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Direct); // AdultA is admin

        var admin = await app.BearerAsync(direct.Secret, HttpMethod.Get, "/api/v1/admin/users");
        Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);
        Assert.Equal(AdminCodes.Required, await CodeOf(admin));

        foreach (var (method, url) in new[]
                 {
                     (HttpMethod.Get, TokenApi.Tokens),
                     (HttpMethod.Post, TokenApi.Tokens),
                     (HttpMethod.Delete, $"{TokenApi.Tokens}/{direct.Token.Id}"),
                     (HttpMethod.Post, "/api/v1/core/me/revoke-sessions"),
                     (HttpMethod.Get, "/api/v1/platform/imports"),
                 })
        {
            var response = await app.BearerAsync(direct.Secret, method, url, method == HttpMethod.Post ? new { name = "x", preset = "read" } : null);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {url} answered {response.StatusCode}");
            Assert.Equal(BrowserOnlyCodes.Required, await CodeOf(response));
        }
    }

    // Review Focus 2.
    [Fact]
    public async Task Revoked_expired_and_forged_tokens_are_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var revoked = await TokenApi.CreateAsync(app, app.AdultA);
        await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{TokenApi.Tokens}/{revoked.Token.Id}");
        var expired = await TokenApi.CreateAsync(app, app.AdultA, days: 1);
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
            await core.PersonalAccessTokens.Where(t => t.Id == expired.Token.Id).ExecuteUpdateAsync(s => s
                .SetProperty(t => t.CreatedAt, NidApp.GoldenCaseToday.AtMidnight().InUtc().ToInstant().Minus(NodaTime.Duration.FromDays(2)))
                .SetProperty(t => t.ExpiresAt, NidApp.GoldenCaseToday.AtMidnight().InUtc().ToInstant().Minus(NodaTime.Duration.FromSeconds(1))));
        var valid = await TokenApi.CreateAsync(app, app.AdultA);
        var wrongSecret = valid.Secret[..^1] + (valid.Secret[^1] == 'A' ? 'B' : 'A');

        foreach (var token in new[] { revoked.Secret, expired.Secret, wrongSecret, "nid_pat_zzzzzzzz_" + new string('A', 43), "nid_pat_nope" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await app.BearerAsync(token, HttpMethod.Get, Capabilities)).StatusCode);
        // A bad token on a write is refused the same way, not by the CSRF check.
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.BearerAsync(revoked.Secret, HttpMethod.Post, "/api/v1/core/persons/dependents", new { displayName = "Kid" })).StatusCode);

        var failures = await AuditApi.EventsAsync(app, SecurityEventNames.TokenAuthFailed);
        Assert.Single(failures); // one per (IP, minute) — the fixed clock never leaves the minute
        Assert.Equal(("system", "api"), (failures[0].ActorKind, failures[0].Via));
        // The first failure is the revoked token's: its prefix matched a row, so the event names its owner (and no secret).
        Assert.DoesNotContain(revoked.Secret[17..], failures[0].Data);
        Assert.Contains(revoked.Token.Prefix, failures[0].Data);
        Assert.Equal(app.AdultA.Value, failures[0].SubjectPersonId);
        Assert.Contains(await AuditApi.EventsAsync(app, SecurityEventNames.TokenAuthFailed), e => e.SubjectPersonId == app.AdultA.Value);
    }

    // Review Focus 5.
    [Fact]
    public async Task A_token_of_a_person_who_cannot_sign_in_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var bobs = await TokenApi.CreateAsync(app, app.AdultB);
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
            await core.Persons.Where(p => p.Id == app.AdultB.Value).ExecuteUpdateAsync(s => s
                .SetProperty(p => p.OidcSubject, (string?)null).SetProperty(p => p.Email, "bob@example.test"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.BearerAsync(bobs.Secret, HttpMethod.Get, Capabilities)).StatusCode);
    }

    // Review Focus 1.
    [Fact]
    public async Task A_bearer_token_wins_over_a_session_and_auth_session_ignores_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var read = await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Read);

        // AdultA's session could write; the read token cannot, and decides alone.
        var write = await app.BearerAsync(read.Secret, HttpMethod.Post, "/api/v1/core/persons/dependents", new { displayName = "Kid" }, withSessionOf: app.AdultA);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

        var session = await (await app.BearerAsync(read.Secret, HttpMethod.Get, "/auth/session")).Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.False(session.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task A_write_made_with_a_token_is_audited_with_its_id_and_via_api()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var propose = await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Direct);

        (await app.BearerAsync(propose.Secret, HttpMethod.Post, "/api/v1/core/persons/dependents", new { displayName = "Kid" })).EnsureSuccessStatusCode();

        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var line = await platform.AuditLog.SingleAsync(r => r.Category == "write" && r.Entity == "core.person" && r.Action == "insert");
        Assert.Equal((propose.Token.Id, "api"), (line.TokenId!.Value, line.Via));
    }

    [Fact]
    public async Task The_61st_request_of_a_minute_is_refused_with_429()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var read = await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Read);

        for (var i = 0; i < 60; i++)
            Assert.Equal(HttpStatusCode.OK, (await app.BearerAsync(read.Secret, HttpMethod.Get, Capabilities)).StatusCode);
        var limited = await app.BearerAsync(read.Secret, HttpMethod.Get, Capabilities);

        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
        // A session is not limited by the token's window.
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Get, Capabilities)).StatusCode);
    }

    [Fact]
    public async Task The_21st_failed_authentication_from_an_address_is_refused_with_429()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var forged = "nid_pat_zzzzzzzz_" + new string('A', 43);

        for (var i = 0; i < 20; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await app.BearerAsync(forged, HttpMethod.Get, Capabilities)).StatusCode);

        Assert.Equal((HttpStatusCode)429, (await app.BearerAsync(forged, HttpMethod.Get, Capabilities)).StatusCode);
    }

    [Fact]
    public async Task Neither_the_secret_nor_the_header_reaches_the_logs()
    {
        var logs = new CollectingLoggerProvider();
        await using var app = await NidApp.StartAsync(fixture, services => services.AddSingleton<ILoggerProvider>(logs));
        var read = await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Read);

        await app.BearerAsync(read.Secret, HttpMethod.Get, Capabilities);
        await app.BearerAsync(read.Secret[..^1] + "x", HttpMethod.Get, Capabilities);

        Assert.DoesNotContain(logs.Messages, m => m.Contains(read.Secret[17..], StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, m => m.Contains("Bearer nid_pat_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_bearer_header_does_not_spare_a_cookie_post_to_auth_logout_its_csrf_check()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var read = await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Read);

        var response = await app.BearerAsync(read.Secret, HttpMethod.Post, "/auth/logout", withSessionOf: app.AdultA);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("code").GetString();

    private static async Task<List<Nid.Core.Infrastructure.Persistence.PersonalAccessTokenRow>> TokenRowsAsync(NidApp app)
    {
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        return await core.PersonalAccessTokens.AsNoTracking().ToListAsync();
    }

    private sealed class CollectingLoggerProvider : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Collector(Messages);

        public void Dispose() { }

        private sealed class Collector(System.Collections.Concurrent.ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Enqueue(formatter(state, exception) + exception);
        }
    }
}
