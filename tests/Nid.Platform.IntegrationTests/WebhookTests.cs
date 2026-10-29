using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Platform.Notifications;
using Nid.TestSupport;

namespace Nid.Platform.IntegrationTests;

/// <summary>Notifications design §5.3 and §6.1: Discord webhooks, stored encrypted, never returned.</summary>
public class WebhookTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Prefs = "/api/v1/platform/notifications/preferences";
    private const string Personal = "/api/v1/platform/notifications/discord";
    private const string Admin = "/api/v1/admin/notifications";
    private const string Url = "https://discord.com/api/webhooks/123456/secretTokenwxyz";

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Put_stores_and_shows_only_the_hint()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var put = await app.RawAsync(app.AdultA, HttpMethod.Put, Personal, new { url = Url });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var view = await JsonOf(put);
        Assert.Equal(("wxyz", "ok"), (view.GetProperty("hint").GetString(), view.GetProperty("status").GetString()));
        Assert.Equal("wxyz", (await app.GetAsync<JsonElement>(app.AdultA, Prefs)).GetProperty("discord").GetProperty("hint").GetString());
        await using var db = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var bytes = (await db.Webhooks.SingleAsync(w => w.PersonId == app.AdultA.Value)).Url;
        Assert.DoesNotContain("secretToken", Encoding.Latin1.GetString(bytes));
    }

    [Fact]
    public async Task Webhook_is_never_returned()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.RawAsync(app.AdultA, HttpMethod.Put, Personal, new { url = Url });
        await app.RawAsync(app.AdultA, HttpMethod.Put, Admin + "/discord", new { url = Url });

        foreach (var route in new[] { Prefs, Admin })
        {
            var text = await (await app.RawAsync(app.AdultA, HttpMethod.Get, route, null)).Content.ReadAsStringAsync();
            Assert.DoesNotContain("secretToken", text);
        }
    }

    [Fact]
    public async Task Invalid_url_is_a_field_error()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, Personal, new { url = "http://example.test/hook" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = Assert.Single((await JsonOf(response)).GetProperty("errors").EnumerateArray());
        Assert.Equal(("url", "notifications.invalid_webhook"), (error.GetProperty("path").GetString(), error.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task Putting_does_not_move_the_preferences_version()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var version = (await app.GetAsync<JsonElement>(app.AdultA, Prefs)).GetProperty("version").GetInt32();

        await app.RawAsync(app.AdultA, HttpMethod.Put, Personal, new { url = Url });
        var save = await app.RawAsync(app.AdultA, HttpMethod.Put, Prefs, new
        {
            emailEnabled = false, action = "immediate", info = "digest", frequency = "weekly", weekday = 1, leadDays = new { }, version,
        });

        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
    }

    [Fact]
    public async Task Delete_removes_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.RawAsync(app.AdultA, HttpMethod.Put, Personal, new { url = Url });

        var delete = await app.RawAsync(app.AdultA, HttpMethod.Delete, Personal, null);

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await app.GetAsync<JsonElement>(app.AdultA, Prefs)).GetProperty("discord").ValueKind);
    }

    [Fact]
    public async Task Household_webhook_is_admin_only()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var refused = await app.RawAsync(app.AdultB, HttpMethod.Put, Admin + "/discord", new { url = Url });
        var accepted = await app.RawAsync(app.AdultA, HttpMethod.Put, Admin + "/discord", new { url = Url });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("wxyz", (await app.GetAsync<JsonElement>(app.AdultA, Admin)).GetProperty("discord").GetProperty("hint").GetString());
        Assert.Equal(JsonValueKind.Null, (await app.GetAsync<JsonElement>(app.AdultA, Prefs)).GetProperty("discord").ValueKind);
    }

    [Fact]
    public async Task Replacing_a_broken_webhook_makes_it_ok()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.RawAsync(app.AdultA, HttpMethod.Put, Personal, new { url = Url });
        await using (var scope = app.Services.CreateAsyncScope())
        {
            // The Replace path has no request actor here; a write is audited as the notifier (the job itself runs as Actor.System, see DiscordChannelTests).
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Notifier(app.AdultA);
            await scope.ServiceProvider.GetRequiredService<IWebhookStore>().MarkBrokenAsync(app.AdultA, Url, default);
        }
        Assert.Equal("broken", (await app.GetAsync<JsonElement>(app.AdultA, Prefs)).GetProperty("discord").GetProperty("status").GetString());

        var put = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Put, Personal, new { url = Url }));

        Assert.Equal("ok", put.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_late_failure_of_the_old_webhook_leaves_the_new_one_ok()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.RawAsync(app.AdultA, HttpMethod.Put, Personal, new { url = Url });
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Notifier(app.AdultA);
        var store = scope.ServiceProvider.GetRequiredService<IWebhookStore>();
        var used = (await store.FindAsync(app.AdultA, default))!.Url; // a send reads it...

        await app.RawAsync(app.AdultA, HttpMethod.Put, Personal, new { url = "https://discord.com/api/webhooks/654321/otherTokenabcd" });
        await store.MarkBrokenAsync(app.AdultA, used, default); // ...and its 404 comes back after the replacement

        Assert.Equal("ok", (await app.GetAsync<JsonElement>(app.AdultA, Prefs)).GetProperty("discord").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Undecryptable_webhook_shows_as_broken_and_can_be_replaced()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.RawAsync(app.AdultA, HttpMethod.Put, Personal, new { url = Url });
        await using (var db = PostgresFixture.NewPlatformContext(app.ConnectionString))
            await db.Webhooks.Where(w => w.PersonId == app.AdultA.Value).ExecuteUpdateAsync(s => s.SetProperty(w => w.Url, new byte[] { 1, 2, 3 }));

        var prefs = await app.RawAsync(app.AdultA, HttpMethod.Get, Prefs, null);

        Assert.Equal(HttpStatusCode.OK, prefs.StatusCode);
        var discord = (await JsonOf(prefs)).GetProperty("discord");
        Assert.Equal(("", "broken"), (discord.GetProperty("hint").GetString(), discord.GetProperty("status").GetString()));
        var put = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Put, Personal, new { url = Url }));
        Assert.Equal("ok", put.GetProperty("status").GetString());
    }
}
