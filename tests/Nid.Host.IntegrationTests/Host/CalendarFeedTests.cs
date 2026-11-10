using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>Issue #80: « À venir » as an iCalendar feed, at an unguessable URL.</summary>
public class CalendarFeedTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Calendar = "/api/v1/core/me/calendar";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Feed(TokenApi.Token Token, string Scope, bool Amounts);
    private sealed record Created(Feed Feed, string Url);
    private sealed record Mine(Feed? Feed);

    [Fact]
    public async Task The_feed_lists_only_the_items_its_person_reads_in_its_scope()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewPricedAsync(app, "Shared streaming", app.AdultA, Visibility.Shared);
        await NewPricedAsync(app, "Alice private gym", app.AdultA, Visibility.Private);
        await NewPricedAsync(app, "Bob private club", app.AdultB, Visibility.Private);

        var household = await CreateAsync(app, app.AdultA, "household");
        var feed = await FeedAsync(app, household.Url);
        Assert.StartsWith("BEGIN:VCALENDAR", feed);
        Assert.Contains("SUMMARY:Renouvellement · Shared streaming", feed);
        Assert.DoesNotContain("Alice private gym", feed);
        Assert.DoesNotContain("Bob private club", feed);
        Assert.DoesNotContain("12\\,50", feed); // no amounts by default

        // Moi: one's own private items too, never the other adult's.
        var me = await PutAsync(app, app.AdultA, "me", amounts: true);
        Assert.Equal(("me", true), (me.Scope, me.Amounts));
        feed = await FeedAsync(app, household.Url); // the same URL
        Assert.Contains("Alice private gym", feed);
        Assert.DoesNotContain("Bob private club", feed);
        Assert.Contains("Shared streaming · 12\\,50 €", feed); // a TEXT value escapes its comma
    }

    [Fact]
    public async Task Regenerating_or_revoking_makes_the_old_url_answer_404_at_once()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var first = await CreateAsync(app, app.AdultA, "household");
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(app, first.Url)).StatusCode);

        var second = await CreateAsync(app, app.AdultA, "household");
        Assert.NotEqual(first.Url, second.Url);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(app, first.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(app, second.Url)).StatusCode);

        // Listed with the other tokens, with its last use; revoked from there like any of them.
        var tokens = await app.GetAsync<List<TokenApi.Token>>(app.AdultA, TokenApi.Tokens);
        var row = Assert.Single(tokens, t => t.Status == "active");
        Assert.Equal((second.Feed.Token.Id, TokenPresets.Calendar), (row.Id, row.Preset));
        Assert.NotNull(row.LastUsedAt);
        (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{TokenApi.Tokens}/{row.Id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(app, second.Url)).StatusCode);
        Assert.Null((await app.GetAsync<Mine>(app.AdultA, Calendar)).Feed);
    }

    [Fact]
    public async Task The_secret_reads_its_feed_and_nothing_else()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var created = await CreateAsync(app, app.AdultA, "me");
        var secret = created.Url[(created.Url.LastIndexOf('/') + 1)..^".ics".Length];
        Assert.StartsWith("nid_cal_", secret);

        // Not a bearer token, on /api or /mcp.
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.BearerAsync(secret, HttpMethod.Get, "/api/v1/platform/capabilities")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.BearerAsync(secret, HttpMethod.Get, "/api/v1/dashboard")).StatusCode);
        // A bearer token is not a feed, and a forged secret is no feed either.
        var bearer = await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Read);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(app, $"/api/v1/calendar/{bearer.Secret}.ics")).StatusCode);
        var forged = secret[..^1] + (secret[^1] == 'A' ? 'B' : 'A');
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(app, $"/api/v1/calendar/{forged}.ics")).StatusCode);
        // A token neither manages a feed nor creates one through « Mes jetons ».
        Assert.Equal(HttpStatusCode.Forbidden, (await app.BearerAsync(bearer.Secret, HttpMethod.Get, Calendar)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.BearerAsync(bearer.Secret, HttpMethod.Post, Calendar, new { scope = "me", amounts = false })).StatusCode);
        var asToken = await app.RawAsync(app.AdultA, HttpMethod.Post, TokenApi.Tokens, new { name = "x", preset = TokenPresets.Calendar });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, asToken.StatusCode);
    }

    [Fact]
    public async Task The_secret_never_reaches_the_logs()
    {
        var logs = new CapturedLogs();
        await using var app = await NidApp.StartAsync(fixture, services => services.AddSingleton<ILoggerProvider>(logs));
        var created = await CreateAsync(app, app.AdultA, "household");
        var secret = created.Url[(created.Url.LastIndexOf('/') + 1)..^".ics".Length];

        await FeedAsync(app, created.Url);
        await GetAsync(app, $"/api/v1/calendar/{secret[..^1]}x.ics"); // a refused one neither

        Assert.NotEmpty(logs.Lines);
        Assert.DoesNotContain(logs.Lines, line => line.Contains(secret[8..], StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, line => line.Contains(secret[17..^1], StringComparison.Ordinal));
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void Dispose() { }

        private sealed class Logger(CapturedLogs logs, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => logs.Lines.Enqueue($"{category}: {formatter(state, exception)} {state}");
        }
    }

    [Fact]
    public async Task A_scope_other_than_household_or_me_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, Calendar, new { scope = "everyone", amounts = false });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Put, Calendar, new { scope = "me", amounts = true })).StatusCode);
    }

    private static async Task NewPricedAsync(NidApp app, string name, PersonId owner, Visibility visibility)
    {
        var id = await app.NewSubscriptionAsync(name, owner, visibility);
        (await app.RawAsync(owner, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn = "2025-01-01",
            price = new { amount = "12.50", currency = "EUR" },
            every = new { count = 1, unit = "year" }, // « À venir » shows the renewal of a long cycle only
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
    }

    private static async Task<Created> CreateAsync(NidApp app, PersonId person, string scope)
    {
        var response = await app.RawAsync(person, HttpMethod.Post, Calendar, new { scope, amounts = false });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Created>(Json))!;
    }

    private static async Task<Feed> PutAsync(NidApp app, PersonId person, string scope, bool amounts)
    {
        var response = await app.RawAsync(person, HttpMethod.Put, Calendar, new { scope, amounts });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Feed>(Json))!;
    }

    private static async Task<HttpResponseMessage> GetAsync(NidApp app, string url)
        => await app.RawAnonymousAsync(HttpMethod.Get, new Uri(url, UriKind.RelativeOrAbsolute).IsAbsoluteUri ? new Uri(url).PathAndQuery : url);

    private static async Task<string> FeedAsync(NidApp app, string url)
    {
        var response = await GetAsync(app, url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar", response.Content.Headers.ContentType?.MediaType);
        return (await response.Content.ReadAsStringAsync()).Replace("\r\n ", "");
    }
}
