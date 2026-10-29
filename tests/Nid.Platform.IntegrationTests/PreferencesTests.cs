using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Notifications;
using Nid.TestSupport;

namespace Nid.Platform.IntegrationTests;

/// <summary>Notifications design §5.2 and §6.1: the person's channel and lead-time preferences.</summary>
public class PreferencesTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Route = "/api/v1/platform/notifications/preferences";

    private sealed class SmtpConfigured : IEmailAvailability
    {
        public bool IsConfigured => true;
    }

    private static object Body(
        bool emailEnabled = false, string action = "immediate", string info = "digest", string frequency = "weekly", int weekday = 1,
        Dictionary<string, int>? leadDays = null, int version = 0)
        => new { emailEnabled, action, info, frequency, weekday, leadDays = leadDays ?? [], version };

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task GiveEmailAsync(NidApp app, PersonId person)
    {
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var row = await core.Persons.SingleAsync(p => p.Id == person.Value);
        row.Email = "a@example.test";
        await core.SaveChangesAsync();
    }

    private static async Task AssertFieldAsync(NidApp app, object body, string path, string code, PersonId? who = null)
    {
        var response = await app.RawAsync(who ?? app.AdultA, HttpMethod.Put, Route, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await JsonOf(response);
        var error = Assert.Single(problem.GetProperty("errors").EnumerateArray());
        Assert.Equal((path, code), (error.GetProperty("path").GetString(), error.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task Defaults_when_nothing_saved()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var prefs = await app.GetAsync<JsonElement>(app.AdultA, Route);

        Assert.False(prefs.GetProperty("emailEnabled").GetBoolean());
        Assert.False(prefs.GetProperty("emailAvailable").GetBoolean());
        Assert.Equal("immediate", prefs.GetProperty("action").GetString());
        Assert.Equal("digest", prefs.GetProperty("info").GetString());
        Assert.Equal("weekly", prefs.GetProperty("frequency").GetString());
        Assert.Equal(1, prefs.GetProperty("weekday").GetInt32());
        Assert.Equal(0, prefs.GetProperty("version").GetInt32());
        var leads = prefs.GetProperty("leadDays").EnumerateArray().ToList();
        var lead = leads.Single(l => l.GetProperty("kind").GetString() == "tenancy_end");
        Assert.Equal((90, 90), (lead.GetProperty("days").GetInt32(), lead.GetProperty("defaultDays").GetInt32()));
        Assert.Equal("Un bail arrive à échéance", lead.GetProperty("title").GetString());
        Assert.DoesNotContain(leads, l => l.GetProperty("kind").GetString() == "rent_arrears");
    }

    [Fact]
    public async Task Save_and_read_back()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, Route,
            Body(action: "digest", info: "centre", frequency: "daily", weekday: 3, leadDays: new() { ["tenancy_end"] = 30, ["loan_end"] = 30 }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await JsonOf(response)).GetProperty("version").GetInt32());
        var prefs = await app.GetAsync<JsonElement>(app.AdultA, Route);
        Assert.Equal(("digest", "centre", "daily", 3, 1), (prefs.GetProperty("action").GetString(), prefs.GetProperty("info").GetString(),
            prefs.GetProperty("frequency").GetString(), prefs.GetProperty("weekday").GetInt32(), prefs.GetProperty("version").GetInt32()));
        var leads = prefs.GetProperty("leadDays").EnumerateArray().ToDictionary(l => l.GetProperty("kind").GetString()!, l => l.GetProperty("days").GetInt32());
        Assert.Equal(30, leads["tenancy_end"]);
        Assert.Equal(30, leads["loan_end"]); // the default

        // Only what differs from the default is stored; another person is unaffected.
        await using var scope = app.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<INotificationStore>().PreferencesAsync(app.AdultA, default);
        Assert.Equal(new Dictionary<string, int> { ["tenancy_end"] = 30 }, stored.LeadDays);
        Assert.Equal(0, (await app.GetAsync<JsonElement>(app.AdultB, Route)).GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Stale_version_is_a_conflict()
    {
        await using var app = await NidApp.StartAsync(fixture);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, Route, Body(version: 0))).StatusCode);

        var stale = await app.RawAsync(app.AdultA, HttpMethod.Put, Route, Body(info: "centre", version: 0));
        var fresh = await app.RawAsync(app.AdultA, HttpMethod.Put, Route, Body(info: "centre", version: 1));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("notifications.preferences_conflict", (await JsonOf(stale)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal(2, (await JsonOf(fresh)).GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Invalid_values_are_field_errors()
    {
        await using var app = await NidApp.StartAsync(fixture);

        await AssertFieldAsync(app, Body(leadDays: new() { ["nope"] = 5 }), "leadDays.nope", "notifications.unknown_kind");
        await AssertFieldAsync(app, Body(leadDays: new() { ["rent_arrears"] = 5 }), "leadDays.rent_arrears", "notifications.unknown_kind");
        await AssertFieldAsync(app, Body(leadDays: new() { ["tenancy_end"] = 366 }), "leadDays.tenancy_end", "notifications.invalid_lead_days");
        await AssertFieldAsync(app, Body(leadDays: new() { ["tenancy_end"] = -1 }), "leadDays.tenancy_end", "notifications.invalid_lead_days");
        await AssertFieldAsync(app, Body(weekday: 0), "weekday", "notifications.invalid_weekday");
        await AssertFieldAsync(app, Body(weekday: 8), "weekday", "notifications.invalid_weekday");
        await AssertFieldAsync(app, Body(emailEnabled: true), "emailEnabled", "notifications.email_unavailable");
        await AssertFieldAsync(app, Body(action: "sometimes"), "action", "request.invalid");
        Assert.Equal(0, (await app.GetAsync<JsonElement>(app.AdultA, Route)).GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Email_needs_an_address_and_smtp()
    {
        await using var notConfigured = await NidApp.StartAsync(fixture);
        await GiveEmailAsync(notConfigured, notConfigured.AdultA);
        await AssertFieldAsync(notConfigured, Body(emailEnabled: true), "emailEnabled", "notifications.email_unavailable");

        await using var app = await NidApp.StartAsync(fixture, services => services.AddSingleton<IEmailAvailability>(new SmtpConfigured()));
        await GiveEmailAsync(app, app.AdultA);

        Assert.True((await app.GetAsync<JsonElement>(app.AdultA, Route)).GetProperty("emailAvailable").GetBoolean());
        Assert.False((await app.GetAsync<JsonElement>(app.AdultB, Route)).GetProperty("emailAvailable").GetBoolean());
        await AssertFieldAsync(app, Body(emailEnabled: true), "emailEnabled", "notifications.email_unavailable", app.AdultB);
        var saved = await app.RawAsync(app.AdultA, HttpMethod.Put, Route, Body(emailEnabled: true));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.True((await JsonOf(saved)).GetProperty("emailEnabled").GetBoolean());
    }
}
