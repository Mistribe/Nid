using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests;

/// <summary>
/// A query parameter the caller sent but that nobody could read used to fall back to a default: the answer
/// was a 200 over a different window, a different horizon or a different scope, with no signal that the
/// question asked was not the question answered. A present-but-malformed value is a bad request; an absent
/// one keeps its documented default.
/// </summary>
public class MalformedInputTests(PostgresFixture fixture, SharedNidApp shared) : IClassFixture<PostgresFixture>, IClassFixture<SharedNidApp>
{
    [Theory]
    [InlineData("?from=oops&to=oops")]
    [InlineData("?from=2025-01-01&to=nope")]
    [InlineData("?horizon=2025-13-45")]
    [InlineData("?from=01/02/2025")]
    [InlineData("?scope=everything")]
    [InlineData("?scope=")]
    public async Task A_malformed_query_parameter_is_a_bad_request_on_the_list(string query)
    {
        var app = shared.App;
        await app.NewMonthlyAsync(app.AdultA, 10m);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/subscriptions" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("?from=oops")]
    [InlineData("?horizon=2025-13-45")]
    public async Task A_malformed_query_parameter_is_a_bad_request_on_the_detail(string query)
    {
        var app = shared.App;
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/subscriptions/{id.Value}" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?scope=me")]
    [InlineData("?scope=Household")]
    [InlineData("?from=2025-01-01&to=2025-02-01&horizon=2025-12-01")]
    public async Task What_the_api_documents_still_answers(string query)
    {
        var app = shared.App;
        await app.NewMonthlyAsync(app.AdultA, 10m);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/subscriptions" + query);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_cancellation_date_nobody_can_read_is_refused_rather_than_dropped()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Patch, $"/api/v1/subscriptions/{id.Value}",
            new { cancelledAt = "31/12/2025" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The silent drop this replaces: the subscription kept renewing while the caller was told 200.
        var detail = await app.GetAsync<SubscriptionApi.DetailResponse>(
            app.AdultA, $"/api/v1/subscriptions/{id.Value}?horizon=2025-12-01");
        Assert.NotNull(detail.NextChargeOn);
    }

    [Fact]
    public async Task Setting_and_clearing_the_cancellation_at_once_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Patch, $"/api/v1/subscriptions/{id.Value}",
            new { cancelledAt = "2025-04-01", clearCancelledAt = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_well_formed_cancellation_still_applies_and_clearing_it_still_works()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);

        (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"/api/v1/subscriptions/{id.Value}",
            new { cancelledAt = "2025-04-01" })).EnsureSuccessStatusCode();
        var cancelled = await app.GetAsync<SubscriptionApi.DetailResponse>(
            app.AdultA, $"/api/v1/subscriptions/{id.Value}?horizon=2025-12-01");
        Assert.All(cancelled.Charges, c => Assert.True(string.CompareOrdinal(c.Date, "2025-04-01") < 0));

        (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"/api/v1/subscriptions/{id.Value}",
            new { clearCancelledAt = true })).EnsureSuccessStatusCode();
        var restored = await app.GetAsync<SubscriptionApi.DetailResponse>(
            app.AdultA, $"/api/v1/subscriptions/{id.Value}?horizon=2025-12-01");
        Assert.Contains(restored.Charges, c => string.CompareOrdinal(c.Date, "2025-04-01") > 0);
    }

    [Fact]
    public async Task The_window_a_malformed_range_used_to_silently_substitute_is_not_returned()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.NewMonthlyAsync(app.AdultA, 10m);

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/subscriptions?from=oops&to=oops");
        var body = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.DoesNotContain("householdCashCost", body, StringComparison.OrdinalIgnoreCase);
    }

    // 1E2b1 design §3.2: the purchase route now reads its body by field, so a malformed grant date is a
    // located 422 (PurchaseRouteTests covers the general shape); this only pins the field itself.
    [Fact]
    public async Task A_malformed_grant_date_is_refused_at_its_path()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("Spotify", app.AdultA, Visibility.Shared);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/purchases", new
        {
            date = "2025-01-01", label = "gift", kind = "gift",
            price = new { amount = "0", currency = "EUR" },
            payer = new { type = "external" },
            grants = new[]
            {
                new
                {
                    subscriptionId = id.Value, duration = new { count = 3, unit = "month" },
                    source = "gift", startsOn = "not-a-date",
                },
            },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("errors");
        Assert.Equal("grants[0].startsOn", Assert.Single(errors.EnumerateArray()).FieldPath());
    }

    /// <summary>#250: RFC 9457 — a fixed title, the sentence in <c>detail</c>, each refused field as <c>{ detail, pointer, code }</c>.</summary>
    [Fact]
    public async Task A_refused_field_answers_rfc_9457_problem_details()
    {
        var app = shared.App;
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/purchases", new
        {
            date = "2025-01-01", label = "gift", kind = "gift",
            price = new { amount = "0", currency = "EUR" },
            payer = new { type = "external" },
            grants = new[]
            {
                new
                {
                    subscriptionId = id.Value, duration = new { count = 3, unit = "month" },
                    source = "gift", startsOn = "not-a-date",
                },
            },
        });

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(422, problem.GetProperty("status").GetInt32());
        Assert.Equal("Validation Error", problem.GetProperty("title").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("detail").GetString()));
        var error = Assert.Single(problem.GetProperty("errors").EnumerateArray());
        Assert.Equal("#/grants/0/startsOn", error.GetProperty("pointer").GetString());
        Assert.Equal("request.invalid", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(error.GetProperty("code").GetString())); // the field's own code (request.date)
        Assert.Equal(problem.GetProperty("detail").GetString(), error.GetProperty("detail").GetString());
        Assert.False(error.TryGetProperty("path", out _));
        Assert.False(error.TryGetProperty("message", out _));
    }

    [Fact]
    public async Task An_unreadable_query_value_is_a_bad_request_with_its_reason_in_detail()
    {
        var app = shared.App;

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/subscriptions?from=oops");

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal("Bad Request", problem.GetProperty("title").GetString());
        Assert.Equal("request.malformed", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("detail").GetString()));
    }
}
