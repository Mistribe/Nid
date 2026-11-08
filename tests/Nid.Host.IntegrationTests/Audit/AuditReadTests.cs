using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Audit;

/// <summary>1F3a spec §5.1, §6 and §9 tests 4, 5 and 7.</summary>
public class AuditReadTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;

    [Fact]
    public async Task A_reader_sees_the_history_of_a_shared_item_newest_first()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("history", app.AdultA, Visibility.Shared);
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"/api/v1/subscriptions/{id.Value}", new { note = "n" })).EnsureSuccessStatusCode();

        var page = Json(await app.GetRawAsync(app.AdultB, $"/api/v1/platform/audit/items/{id.Value}"));

        var entries = page.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal("update", entries[0].GetProperty("action").GetString());
        Assert.Contains(entries, e => e.GetProperty("entity").GetString() == "core.item");
        Assert.Equal(JsonValueKind.Null, page.GetProperty("next").ValueKind);
    }

    [Fact]
    public async Task The_history_of_a_hidden_item_is_a_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await app.NewSubscriptionAsync("hidden", app.AdultA, Visibility.Private);

        var response = await app.RawAsync(app.AdultB, HttpMethod.Get, $"/api/v1/platform/audit/items/{hidden.Value}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_purchase_that_also_feeds_a_hidden_item_stays_out_of_the_history()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewSubscriptionAsync("shared", app.AdultA, Visibility.Shared);
        var hidden = await app.NewSubscriptionAsync("hidden", app.AdultA, Visibility.Private);
        await app.SeedPurchaseGrantingToAsync(shared, hidden);

        var forB = await app.RawAsync(app.AdultB, HttpMethod.Get, $"/api/v1/platform/audit/items/{shared.Value}");
        var forA = Json(await app.GetRawAsync(app.AdultA, $"/api/v1/platform/audit/items/{shared.Value}"));

        Assert.Equal(HttpStatusCode.OK, forB.StatusCode);
        var body = await forB.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("subscriptions.purchase", body);
        Assert.DoesNotContain(hidden.Value.ToString(), body);
        Assert.Contains(forA.GetProperty("entries").EnumerateArray(), e => e.GetProperty("entity").GetString() == "subscriptions.purchase");
        Assert.Empty(await AuditApi.EventsAsync(app, "access_violation"));
    }

    /// <summary>A funding row names its goal and its envelope: B's history of a shared goal never shows A's private envelope.</summary>
    [Fact]
    public async Task A_goal_link_to_a_hidden_envelope_stays_out_of_the_history()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await app.NewEnvelopeAsync("PEA de A", app.AdultA, Visibility.Private);
        var goal = await app.NewGoalAsync("Voyage", app.AdultA, Visibility.Shared, [(hidden, 5000)]);

        var forB = await app.RawAsync(app.AdultB, HttpMethod.Get, $"/api/v1/platform/audit/items/{goal.Value}");
        var forA = Json(await app.GetRawAsync(app.AdultA, $"/api/v1/platform/audit/items/{goal.Value}"));

        Assert.Equal(HttpStatusCode.OK, forB.StatusCode);
        var body = await forB.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(hidden.Value.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(forA.GetProperty("entries").EnumerateArray(), e => e.GetProperty("entity").GetString() == "goals.funding");
        Assert.Contains(Json(await app.GetRawAsync(app.AdultA, $"/api/v1/platform/audit/items/{hidden.Value}")).GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("entity").GetString() == "goals.funding");
    }

    [Fact]
    public async Task Pages_follow_the_cursor()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("paged", app.AdultA, Visibility.Shared);
        for (var i = 0; i < 3; i++)
            (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"/api/v1/subscriptions/{id.Value}", new { note = $"n{i}" })).EnsureSuccessStatusCode();

        var first = Json(await app.GetRawAsync(app.AdultA, $"/api/v1/platform/audit/items/{id.Value}?limit=2"));
        var next = first.GetProperty("next").GetInt64();
        var second = Json(await app.GetRawAsync(app.AdultA, $"/api/v1/platform/audit/items/{id.Value}?limit=2&before={next}"));

        Assert.Equal(2, first.GetProperty("entries").GetArrayLength());
        Assert.True(second.GetProperty("entries").EnumerateArray().All(e => e.GetProperty("seq").GetInt64() < next));
    }

    [Fact]
    public async Task My_events_are_mine_only()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.RawAsync(app.AdultA, HttpMethod.Post, "/auth/logout");
        await app.RawAsync(app.AdultB, HttpMethod.Post, "/auth/logout");

        var page = Json(await app.GetRawAsync(app.AdultB, "/api/v1/platform/audit/me/security"));

        var entry = Assert.Single(page.GetProperty("entries").EnumerateArray());
        Assert.Equal(("logout", app.AdultB.Value), (entry.GetProperty("event").GetString(), entry.GetProperty("subjectPersonId").GetGuid()));
    }

    [Fact]
    public async Task The_journal_is_the_admins_and_leaves_out_other_adults_own_sessions()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.RawAsync(app.AdultB, HttpMethod.Post, "/auth/logout");
        await app.RawAsync(app.AdultB, HttpMethod.Post, "/api/v1/core/me/revoke-sessions");
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/admin/users/{app.AdultB.Value}/revoke-sessions")).EnsureSuccessStatusCode();
        var hidden = await app.NewSubscriptionAsync("hidden", app.AdultA, Visibility.Private);
        await app.RawAsync(app.AdultB, HttpMethod.Get, $"/api/v1/subscriptions/{hidden.Value}");

        var refused = await app.RawAsync(app.AdultB, HttpMethod.Get, "/api/v1/admin/security-journal");
        var journal = Json(await app.GetRawAsync(app.AdultA, "/api/v1/admin/security-journal"));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var entry = Assert.Single(journal.GetProperty("entries").EnumerateArray());
        Assert.Equal("session_revoked", entry.GetProperty("event").GetString());
        Assert.Equal(app.AdultA.Value, entry.GetProperty("actor").GetProperty("personId").GetGuid());
    }

    /// <summary>T5b: the filter never widens what the journal shows, even for an event that really happened.</summary>
    [Theory]
    [InlineData("logout")]
    [InlineData("access_denied")]
    public async Task Filtering_on_an_event_outside_the_journal_is_an_empty_page(string filtered)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await app.NewSubscriptionAsync("hidden", app.AdultA, Visibility.Private);
        await app.RawAsync(app.AdultA, HttpMethod.Post, "/auth/logout");
        await app.RawAsync(app.AdultB, HttpMethod.Get, $"/api/v1/subscriptions/{hidden.Value}");
        Assert.NotEmpty(await AuditApi.EventsAsync(app, "logout"));
        Assert.NotEmpty(await AuditApi.EventsAsync(app, "access_denied"));

        var page = Json(await app.GetRawAsync(app.AdultA, $"/api/v1/admin/security-journal?event={filtered}"));

        Assert.Empty(page.GetProperty("entries").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("next").ValueKind);
    }

    [Fact]
    public async Task The_journal_names_the_items_outside_the_visible_set_only_when_the_admin_may_see_them()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await app.NewSubscriptionAsync("hidden", app.AdultB, Visibility.Private);
        var shown = await app.NewSubscriptionAsync("shown", app.AdultB, Visibility.Shared);
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IAccessViolations>().RecordAsync(
                "ProbeQuery", app.AdultB, [hidden, shown], new AccessViolationCause(AccessViolationReasons.OutsideVisibleSet), TestContext.Current.CancellationToken);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/admin/security-journal?event=access_violation");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("\"hidden\"", body);
        var entry = Assert.Single(Json(body).GetProperty("entries").EnumerateArray());
        var details = entry.GetProperty("details");
        Assert.Equal("ProbeQuery", details.GetProperty("request").GetString());
        Assert.Equal("outside_visible_set", details.GetProperty("reason").GetString());
        Assert.Equal("2", details.GetProperty("count").GetString());
        var items = entry.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, items[hidden.Value].GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, items[hidden.Value].GetProperty("kind").ValueKind);
        Assert.Equal("shown", items[shown.Value].GetProperty("name").GetString());
        Assert.Equal("subscription", items[shown.Value].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_lookup_failure_shows_the_type_of_its_exception_never_its_message_nor_any_item()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("kept", app.AdultA, Visibility.Shared);
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IAccessViolations>().RecordAsync(
                "ProbeQuery", app.AdultA, [item],
                new AccessViolationCause(AccessViolationReasons.VisibilityLookupFailed, new TimeoutException("secret detail")), TestContext.Current.CancellationToken);

        var body = await (await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/admin/security-journal?event=access_violation"))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("secret detail", body);
        var entry = Assert.Single(Json(body).GetProperty("entries").EnumerateArray());
        Assert.Equal("TimeoutException", entry.GetProperty("details").GetProperty("error").GetString());
        Assert.Empty(entry.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task An_older_violation_without_a_reason_still_reads()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ISecurityEvents>().RecordAsync(
                new SecurityEvent(SecurityEventNames.AccessViolation, app.AdultA, [], new Dictionary<string, string> { ["request"] = "OldQuery" }),
                Actor.Browser(app.AdultA), TestContext.Current.CancellationToken);

        var journal = Json(await app.GetRawAsync(app.AdultA, "/api/v1/admin/security-journal?event=access_violation"));

        var entry = Assert.Single(journal.GetProperty("entries").EnumerateArray());
        Assert.Equal("OldQuery", entry.GetProperty("details").GetProperty("request").GetString());
        Assert.False(entry.GetProperty("details").TryGetProperty("reason", out _));
    }
}
