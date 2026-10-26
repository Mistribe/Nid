using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Platform.Infrastructure.Persistence;
using Nid.Platform.Notifications;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests.Notifications;

/// <summary>Notifications design §4: the centre's API.</summary>
public class NotificationEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Route = "/api/v1/platform/notifications";
    private static readonly LocalDate Today = NidApp.GoldenCaseToday;

    private static NotificationKind Kind(NotificationLevel level = NotificationLevel.Action)
        => new("subscription_renewal", "due_date", level, "subscriptions", 7);

    private static async Task<Guid> SeedAsync(NidApp app, PersonId person, ItemId item, string key, NotificationLevel level = NotificationLevel.Action,
        LocalDate? dueOn = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INotificationStore>();
        var signal = new Signal(key, "subscription_renewal", [item], dueOn ?? Today.PlusDays(3), new Dictionary<string, string>());
        await store.ApplyAsync(person, [new LifecycleChange.Insert(signal, Kind(level), Today, true)], default);
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        return await db.Notifications.AsNoTracking().Where(n => n.PersonId == person.Value && n.Key == key).Select(n => n.Id).SingleAsync();
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<JsonElement> ListAsync(NidApp app, PersonId who, string list = "todo")
        => await app.GetAsync<JsonElement>(who, $"{Route}?list={list}");

    private static async Task<int> UnreadAsync(NidApp app, PersonId who)
        => (await app.GetAsync<JsonElement>(who, $"{Route}/count")).GetProperty("unread").GetInt32();

    private static async Task<string> StateOfAsync(NidApp app, Guid id)
    {
        await using var db = PostgresFixture.NewPlatformContext(app.ConnectionString);
        return await db.Notifications.AsNoTracking().Where(n => n.Id == id).Select(n => n.State).SingleAsync();
    }

    private static async Task<Instant?> ReadAtAsync(NidApp app, Guid id)
    {
        await using var db = PostgresFixture.NewPlatformContext(app.ConnectionString);
        return await db.Notifications.AsNoTracking().Where(n => n.Id == id).Select(n => n.ReadAt).SingleAsync();
    }

    private static async Task SetVisibilityAsync(NidApp app, ItemId item, string visibility)
    {
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var row = await core.Items.SingleAsync(i => i.Id == item.Value);
        row.Visibility = visibility;
        await core.SaveChangesAsync();
    }

    private static Task<HttpResponseMessage> PostAsync(NidApp app, PersonId who, Guid id, string action, object? body = null)
        => app.RawAsync(who, HttpMethod.Post, $"{Route}/{id}/{action}", body);

    [Fact]
    public async Task List_and_count()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        var id = await SeedAsync(app, app.AdultA, item, "a", dueOn: new LocalDate(2025, 10, 18));

        var list = await ListAsync(app, app.AdultA);

        var entry = Assert.Single(list.GetProperty("entries").EnumerateArray());
        Assert.Equal(id, entry.GetProperty("id").GetGuid());
        Assert.Equal("Un abonnement se renouvelle", entry.GetProperty("title").GetString());
        Assert.Equal("Netflix — le 18 octobre 2025", entry.GetProperty("body").GetString());
        Assert.Equal($"/subscriptions/{item.Value:D}", entry.GetProperty("link").GetString());
        Assert.Equal(("action", "open", false, false), (entry.GetProperty("level").GetString(), entry.GetProperty("state").GetString(),
            entry.GetProperty("read").GetBoolean(), entry.GetProperty("critical").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, list.GetProperty("next").ValueKind);
        Assert.Equal(1, await UnreadAsync(app, app.AdultA));
        Assert.Equal(0, await UnreadAsync(app, app.AdultB));
        Assert.Empty((await ListAsync(app, app.AdultA, "snoozed")).GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task The_list_pages_and_a_foreign_cursor_gives_an_empty_page()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        for (var i = 0; i < 3; i++) await SeedAsync(app, app.AdultA, item, $"k{i}");
        var foreign = await SeedAsync(app, app.AdultB, item, "b");

        var first = await app.GetAsync<JsonElement>(app.AdultA, $"{Route}?list=todo&limit=2");
        var next = first.GetProperty("next").GetGuid();
        var second = await app.GetAsync<JsonElement>(app.AdultA, $"{Route}?list=todo&limit=2&before={next}");
        var strange = await app.GetAsync<JsonElement>(app.AdultA, $"{Route}?list=todo&before={foreign}");

        Assert.Equal(2, first.GetProperty("entries").GetArrayLength());
        Assert.Single(second.GetProperty("entries").EnumerateArray());
        Assert.Empty(strange.GetProperty("entries").EnumerateArray());
        var bad = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Route}?list=nope");
        Assert.True(bad.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Row_hidden_while_an_item_is_unreadable()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        var id = await SeedAsync(app, app.AdultB, item, "a");
        Assert.Equal(1, await UnreadAsync(app, app.AdultB));

        await SetVisibilityAsync(app, item, "private");
        Assert.Empty((await ListAsync(app, app.AdultB)).GetProperty("entries").EnumerateArray());
        Assert.Equal(0, await UnreadAsync(app, app.AdultB));
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(app, app.AdultB, id, "read")).StatusCode);

        await SetVisibilityAsync(app, item, "shared");
        Assert.Single((await ListAsync(app, app.AdultB)).GetProperty("entries").EnumerateArray());
        Assert.Equal(1, await UnreadAsync(app, app.AdultB));
    }

    [Fact]
    public async Task Another_persons_notification_is_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        var id = await SeedAsync(app, app.AdultA, item, "a");

        foreach (var (action, body) in new (string, object?)[] { ("read", null), ("snooze", new { preset = "week" }), ("dismiss", null) })
            Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(app, app.AdultB, id, action, body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(app, app.AdultA, Guid.NewGuid(), "read")).StatusCode);
        Assert.Equal(NotificationStates.Open, await StateOfAsync(app, id));
    }

    [Fact]
    public async Task Snooze_until_is_bounded()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        var id = await SeedAsync(app, app.AdultA, item, "a");
        string Day(int plus) => Today.PlusDays(plus).ToString("yyyy-MM-dd", null);

        foreach (var body in new object[]
                 {
                     new { until = Day(0) }, new { until = Day(-1) }, new { until = Day(366) }, new { until = Day(3), preset = "week" }, new { },
                 })
        {
            var response = await PostAsync(app, app.AdultA, id, "snooze", body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var problem = await JsonOf(response);
            Assert.Equal("notifications.invalid_snooze", problem.GetProperty("code").GetString());
            Assert.Equal("until", problem.GetProperty("errors")[0].GetProperty("path").GetString());
        }
        Assert.Equal(NotificationStates.Open, await StateOfAsync(app, id));

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, app.AdultA, id, "snooze", new { until = Day(7) })).StatusCode);
        Assert.Equal(NotificationStates.Snoozed, await StateOfAsync(app, id));
        Assert.Single((await ListAsync(app, app.AdultA, "snoozed")).GetProperty("entries").EnumerateArray());

        // A snoozed row can be snoozed again, to another date.
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, app.AdultA, id, "snooze", new { preset = "month" })).StatusCode);
        var entry = (await ListAsync(app, app.AdultA, "snoozed")).GetProperty("entries")[0];
        Assert.Equal("2025-11-15", entry.GetProperty("snoozedUntil").GetString());
    }

    [Fact]
    public async Task Critical_cannot_be_snoozed_or_dismissed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        var id = await SeedAsync(app, app.AdultA, item, "a", NotificationLevel.Critical);

        var snooze = await PostAsync(app, app.AdultA, id, "snooze", new { preset = "week" });
        var dismiss = await PostAsync(app, app.AdultA, id, "dismiss");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, snooze.StatusCode);
        Assert.Equal("notifications.not_snoozable", (await JsonOf(snooze)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, dismiss.StatusCode);
        Assert.Equal("notifications.not_dismissable", (await JsonOf(dismiss)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, app.AdultA, id, "read")).StatusCode);
        Assert.Equal(0, await UnreadAsync(app, app.AdultA));
    }

    [Fact]
    public async Task Dismiss_moves_the_row_to_the_history_and_a_dismissed_row_is_not_actionable()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        var id = await SeedAsync(app, app.AdultA, item, "a");

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, app.AdultA, id, "dismiss")).StatusCode);

        Assert.Equal(NotificationStates.Dismissed, await StateOfAsync(app, id));
        Assert.Single((await ListAsync(app, app.AdultA, "history")).GetProperty("entries").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(app, app.AdultA, id, "dismiss")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(app, app.AdultA, id, "snooze", new { preset = "week" })).StatusCode);
    }

    [Fact]
    public async Task A_snoozed_row_can_be_dismissed_and_only_open_or_snoozed_rows_can_be_read()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        var id = await SeedAsync(app, app.AdultA, item, "a");
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, app.AdultA, id, "snooze", new { preset = "week" })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, app.AdultA, id, "dismiss")).StatusCode);

        Assert.Equal(NotificationStates.Dismissed, await StateOfAsync(app, id));
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(app, app.AdultA, id, "read")).StatusCode);
    }

    [Fact]
    public async Task Read_all_reads_only_what_is_visible()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        var hidden = await app.NewSubscriptionAsync("Secret", app.AdultA, Visibility.Shared);
        await SeedAsync(app, app.AdultB, shared, "a");
        var hiddenRow = await SeedAsync(app, app.AdultB, hidden, "b");
        await SetVisibilityAsync(app, hidden, "private");

        var response = await app.RawAsync(app.AdultB, HttpMethod.Post, $"{Route}/read-all");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await UnreadAsync(app, app.AdultB));
        Assert.Null(await ReadAtAsync(app, hiddenRow));
        await SetVisibilityAsync(app, hidden, "shared");
        Assert.Equal(1, await UnreadAsync(app, app.AdultB)); // still unread: the reader could not see it
    }

    [Fact]
    public async Task Every_catalogued_kind_renders()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<NotificationCatalog>();
        var item = new ItemId(Guid.NewGuid());

        Assert.Equal(19, catalog.Kinds.Count);
        foreach (var kind in catalog.Kinds.Values)
        {
            var record = new NotificationRecord(Guid.NewGuid(), app.AdultA, kind.Kind, kind.Kind, kind.Level, [item], null, Today,
                new Dictionary<string, string>(), NotificationStates.Open, null, null, Instant.FromUtc(2025, 10, 15, 0, 0), null, null);
            var rendered = NotificationTemplates.Render(record, kind, new Dictionary<ItemId, string> { [item] = "Bien" });
            Assert.NotEmpty(rendered.Title);
            Assert.StartsWith("/", rendered.Link);
        }
    }
}
