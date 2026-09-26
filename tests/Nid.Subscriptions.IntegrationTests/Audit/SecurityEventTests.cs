using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.Platform;
using Nid.Platform.Contracts;
using Nid.Platform.Infrastructure.Audit;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Audit;

/// <summary>1F3a spec §5 and §9 test 6: each event where stated, once, with identifiers only.</summary>
[Collection("postgres")]
public class SecurityEventTests(PostgresFixture fixture)
{
    public static async Task<List<AuditEntryRow>> EventsAsync(NidApp app, string name)
    {
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        return await platform.AuditLog.Where(r => r.Category == "security" && r.Action == name).OrderBy(r => r.Seq).ToListAsync();
    }

    [Fact]
    public async Task Reading_an_item_one_cannot_see_is_recorded_as_access_denied()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await app.NewSubscriptionAsync("hidden", app.AdultA, Visibility.Private);

        var response = await app.RawAsync(app.AdultB, HttpMethod.Get, $"/api/v1/subscriptions/{hidden.Value}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var denied = Assert.Single(await EventsAsync(app, "access_denied"));
        Assert.Equal((app.AdultB.Value, app.AdultB.Value), (denied.PersonId!.Value, denied.SubjectPersonId!.Value));
        Assert.Equal([hidden.Value], denied.ItemIds);
        Assert.Equal("GetSubscriptionQuery", denied.Request);
    }

    [Fact]
    public async Task Repeated_refusals_on_the_same_item_are_recorded_once_a_minute()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await app.NewSubscriptionAsync("hidden", app.AdultA, Visibility.Private);

        for (var i = 0; i < 20; i++)
            await app.RawAsync(app.AdultB, HttpMethod.Get, $"/api/v1/subscriptions/{hidden.Value}");

        Assert.Single(await EventsAsync(app, "access_denied"));
    }

    /// <summary>Important #1: an aborted request's cancellation must not lose the event it caused (spec U10).</summary>
    [Fact]
    public async Task An_access_denied_is_recorded_even_if_the_caller_s_token_is_already_cancelled()
    {
        await using var app = await NidApp.StartAsync(fixture);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        bool recorded;
        await using (var scope = app.Services.CreateAsyncScope())
            recorded = await scope.ServiceProvider.GetRequiredService<ISecurityEvents>().RecordAsync(
                new SecurityEvent(SecurityEventNames.AccessDenied, app.AdultB, [], new Dictionary<string, string> { ["access"] = "read" }),
                Actor.Browser(app.AdultB),
                cts.Token);

        Assert.True(recorded);
        Assert.Single(await EventsAsync(app, "access_denied"));
    }

    /// <summary>Important #1: a write that fails must give its throttle slot back, or the whole minute goes dark.</summary>
    [Fact]
    public async Task A_refusal_lost_to_a_failed_write_does_not_block_the_next_one_in_the_same_minute()
    {
        // A fresh scope per request builds a fresh ISecurityEvents, so "the first write fails" must be
        // tracked outside any one scope: a singleton gate shared by every request this host serves.
        var gate = new FlakyGate();
        await using var app = await NidApp.StartAsync(fixture, services =>
        {
            services.AddSingleton(gate);
            services.AddScoped<ISecurityEvents>(sp => new FlakyOnceSecurityEvents(new SecurityEventWriter(
                sp.GetRequiredService<Npgsql.NpgsqlDataSource>(), sp.GetRequiredService<IActorAccessor>(),
                sp.GetRequiredService<Nid.Platform.Audit.AuditRequest>(), NodaTime.SystemClock.Instance,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SecurityEventWriter>>()), gate));
        });
        var hidden = await app.NewSubscriptionAsync("hidden", app.AdultA, Visibility.Private);

        // Same actor, same item, same (fixed) clock minute: the first refusal's write fails and must not
        // consume the minute's only slot, or the second refusal below would be silently dropped too.
        await app.RawAsync(app.AdultB, HttpMethod.Get, $"/api/v1/subscriptions/{hidden.Value}");
        await app.RawAsync(app.AdultB, HttpMethod.Get, $"/api/v1/subscriptions/{hidden.Value}");

        Assert.Single(await EventsAsync(app, "access_denied"));
    }

    private sealed class FlakyGate
    {
        public int Calls;
    }

    private sealed class FlakyOnceSecurityEvents(ISecurityEvents inner, FlakyGate gate) : ISecurityEvents
    {
        public async Task<bool> RecordAsync(SecurityEvent securityEvent, CancellationToken ct)
        {
            // The first write is lost outright (as if the connection had failed) and never reaches inner.
            if (Interlocked.Increment(ref gate.Calls) == 1)
                return false;
            return await inner.RecordAsync(securityEvent, ct);
        }

        public Task<bool> RecordAsync(SecurityEvent securityEvent, Actor actor, CancellationToken ct)
            => inner.RecordAsync(securityEvent, actor, ct);
    }

    [Fact]
    public async Task An_access_violation_is_recorded_and_still_logged()
    {
        await using var app = await NidApp.StartAsync(fixture, services =>
            services.AddScoped<IVisibleItems>(sp => new EmptyForMeVisibleItems(sp.GetRequiredService<CoreDbContext>())));
        var id = await app.NewSubscriptionAsync("leaky", app.AdultA, Visibility.Shared);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/subscriptions?scope=household");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var violation = Assert.Single(await EventsAsync(app, "access_violation"));
        Assert.Contains(id.Value, violation.ItemIds);
        Assert.Equal("ListSubscriptionsQuery", JsonDocument.Parse(violation.Data).RootElement.GetProperty("request").GetString());
    }

    [Fact]
    public async Task Admin_area_commands_record_their_event_on_success_only()
    {
        await using var app = await NidApp.StartAsync(fixture);

        (await app.RawAsync(app.AdultA, HttpMethod.Put, $"/api/v1/admin/users/{app.AdultB.Value}/admin", new { isAdmin = true })).EnsureSuccessStatusCode();
        (await app.RawAsync(app.AdultA, HttpMethod.Put, $"/api/v1/admin/users/{app.AdultB.Value}/admin", new { isAdmin = false })).EnsureSuccessStatusCode();
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/admin/users/{app.AdultB.Value}/revoke-sessions")).EnsureSuccessStatusCode();
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, "/api/v1/admin/household", new { maxAdults = 3 })).EnsureSuccessStatusCode();
        var added = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/admin/users", new { displayName = "Carol", email = "carol@example.test" });
        added.EnsureSuccessStatusCode();
        var carol = JsonDocument.Parse(await added.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"/api/v1/admin/users/{carol}")).EnsureSuccessStatusCode();
        // Refused (the last active admin cannot be demoted): no event.
        Assert.False((await app.RawAsync(app.AdultA, HttpMethod.Put, $"/api/v1/admin/users/{app.AdultA.Value}/admin", new { isAdmin = false })).IsSuccessStatusCode);

        Assert.Equal(app.AdultB.Value, Assert.Single(await EventsAsync(app, "admin_granted")).SubjectPersonId!.Value);
        Assert.Equal(app.AdultB.Value, Assert.Single(await EventsAsync(app, "admin_revoked")).SubjectPersonId!.Value);
        Assert.Equal(app.AdultB.Value, Assert.Single(await EventsAsync(app, "session_revoked")).SubjectPersonId!.Value);
        Assert.Equal(carol, Assert.Single(await EventsAsync(app, "person_preregistered")).SubjectPersonId!.Value);
        Assert.Equal(carol, Assert.Single(await EventsAsync(app, "person_preregistration_cancelled")).SubjectPersonId!.Value);
        var settings = Assert.Single(await EventsAsync(app, "household_settings_changed"));
        Assert.Equal("max_adults", JsonDocument.Parse(settings.Data).RootElement.GetProperty("fields").GetString());
        Assert.All(await AllSecurityAsync(app), e => Assert.DoesNotContain("carol@example.test", e.Data));
    }

    /// <summary>Minor #4: a `PATCH` naming neither field changed nothing, so it is not worth an event.</summary>
    [Fact]
    public async Task A_household_patch_naming_no_field_records_no_event()
    {
        await using var app = await NidApp.StartAsync(fixture);

        (await app.RawAsync(app.AdultA, HttpMethod.Patch, "/api/v1/admin/household", new { })).EnsureSuccessStatusCode();

        Assert.Empty(await EventsAsync(app, "household_settings_changed"));
    }

    [Fact]
    public async Task An_adult_signs_themselves_out_everywhere()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var before = await app.EpochOfAsync(app.AdultB);

        var response = await app.RawAsync(app.AdultB, HttpMethod.Post, "/api/v1/core/me/revoke-sessions");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(before + 1, await app.EpochOfAsync(app.AdultB));
        var revoked = Assert.Single(await EventsAsync(app, "session_revoked"));
        Assert.Equal((app.AdultB.Value, app.AdultB.Value, "RevokeMySessionsCommand"), (revoked.PersonId!.Value, revoked.SubjectPersonId!.Value, revoked.Request));
    }

    [Fact]
    public async Task A_security_event_that_cannot_be_written_does_not_fail_the_request()
    {
        await using var app = await NidApp.StartAsync(fixture, services =>
            services.AddScoped<ISecurityEvents>(sp => new SecurityEventWriter(
                new Npgsql.NpgsqlDataSourceBuilder("Host=127.0.0.1;Port=1;Timeout=1").Build(),
                sp.GetRequiredService<IActorAccessor>(), sp.GetRequiredService<Nid.Platform.Audit.AuditRequest>(),
                NodaTime.SystemClock.Instance, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SecurityEventWriter>>())));
        var hidden = await app.NewSubscriptionAsync("hidden", app.AdultA, Visibility.Private);

        var response = await app.RawAsync(app.AdultB, HttpMethod.Get, $"/api/v1/subscriptions/{hidden.Value}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<List<AuditEntryRow>> AllSecurityAsync(NidApp app)
    {
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        return await platform.AuditLog.Where(r => r.Category == "security").ToListAsync();
    }

    /// <summary>
    /// Truthful where the handler looks (the list's own scope), empty where the guard looks (<c>Me</c>) — the
    /// leak bench's own model (<c>LeakBenchTests.GenerousInTheHouseholdScope</c>), inverted: here it is the
    /// guard, not the handler, that must be fooled into refusing a response that would otherwise be legitimate,
    /// so the violation path is exercised without an empty list masking it.
    /// </summary>
    private sealed class EmptyForMeVisibleItems(CoreDbContext db) : IVisibleItems
    {
        public async Task<IReadOnlySet<ItemId>> ForAsync(PersonId actor, VisibilityScope scope, CancellationToken ct)
            => scope == VisibilityScope.Me
                ? new HashSet<ItemId>()
                : await new VisibleItems(db).ForAsync(actor, scope, ct);
    }
}
