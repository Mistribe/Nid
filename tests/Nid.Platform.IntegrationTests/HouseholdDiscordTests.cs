using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Platform.Infrastructure.Persistence;
using Nid.Platform.Notifications;
using Nid.Platform.Outbound;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Platform.IntegrationTests;

/// <summary>Notifications design §5.3: the household Discord channel, one post per key for the whole household.</summary>
public class HouseholdDiscordTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Hook = "https://discord.com/api/webhooks/777/householdTokenabcd";
    private const string AdminRoute = "/api/v1/admin/notifications";

    private static readonly NotificationKind Action = new("fake_action", "finding", NotificationLevel.Action, "fake", null);
    private static readonly NotificationKind Critical = new("fake_critical", "finding", NotificationLevel.Critical, "fake", null);
    private static readonly NotificationKind Info = new("fake_info", "finding", NotificationLevel.Info, "fake", null);
    private static readonly NotificationKind Routine = new("routine_due", "finding", NotificationLevel.Action, "fake", null);

    private sealed class Recorder : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        public List<(Uri Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return Answer(request);
        }
    }

    private sealed class SettableClock(Instant now) : IClock
    {
        public Instant Now { get; set; } = now;

        public Instant GetCurrentInstant() => Now;
    }

    private sealed class FakeSource : INotificationSource
    {
        public string Name => "fake";

        public IReadOnlyList<NotificationKind> Kinds { get; } = [Action, Critical, Info, Routine];

        public List<Signal> Signals { get; } = [];

        public Task<Result<IReadOnlyList<Signal>>> SignalsAsync(PersonId reader, LocalDate today, LocalDate horizon, CancellationToken ct)
            => Task.FromResult(Result.Success<IReadOnlyList<Signal>>(Signals.ToList()));
    }

    private sealed record Harness(NidApp App, FakeSource Source, Recorder Discord, SettableClock Clock) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private async Task<Harness> StartAsync(bool webhook = true)
    {
        var source = new FakeSource();
        var discord = new Recorder();
        var clock = new SettableClock(Instant.FromUtc(2025, 10, 15, 8, 0));
        var app = await NidApp.StartAsync(
            fixture,
            services =>
            {
                services.AddSingleton<IClock>(clock);
                services.RemoveAll<INotificationSource>();
                services.AddSingleton<INotificationSource>(source);
                services.RemoveAll<SafeHttpClient>();
                services.AddSingleton(new SafeHttpClient(new HttpMessageInvoker(discord), new OutboundOptions(), NullLogger<SafeHttpClient>.Instance));
            });
        if (webhook)
            Assert.True((await app.RawAsync(app.AdultA, HttpMethod.Put, AdminRoute + "/discord", new { url = Hook })).IsSuccessStatusCode);
        return new Harness(app, source, discord, clock);
    }

    private static Signal SignalOf(NotificationKind kind, string key, params ItemId[] items)
        => new(key, kind.Kind, items, null, new Dictionary<string, string>());

    private static async Task EvaluateAllAsync(Harness h)
    {
        foreach (var person in new[] { h.App.AdultA, h.App.AdultB })
        {
            var (_, failure) = await h.App.InvokeAsync(h.App.AdultA, new EvaluateNotificationsCommand(person, true));
            Assert.Null(failure);
        }
    }

    private static async Task DispatchAsync(Harness h)
    {
        var (_, failure) = await h.App.InvokeAsync(h.App.AdultA, new DispatchNotificationsCommand());
        Assert.Null(failure);
    }

    private static async Task<List<NotificationDeliveryRow>> HouseholdAsync(Harness h)
    {
        await using var db = PostgresFixture.NewPlatformContext(h.App.ConnectionString);
        return await db.Deliveries.AsNoTracking().Where(d => d.Channel == "discord_household").ToListAsync();
    }

    private static Task<ItemId> SharedAsync(Harness h, string name = "Appartement Lyon")
        => h.App.NewSubscriptionAsync(name, h.App.AdultA, Visibility.Shared);

    private static async Task SetVisibilityAsync(Harness h, ItemId item, string visibility)
    {
        await using var core = PostgresFixture.NewCoreContext(h.App.ConnectionString);
        var row = await core.Items.SingleAsync(i => i.Id == item.Value);
        row.Visibility = visibility;
        await core.SaveChangesAsync();
    }

    [Fact]
    public async Task Shared_action_row_posts_to_the_household()
    {
        await using var h = await StartAsync();
        h.Source.Signals.Add(SignalOf(Action, "a:1", await SharedAsync(h)));

        await EvaluateAllAsync(h);

        Assert.Single(await HouseholdAsync(h));
        await DispatchAsync(h);
        var (url, body) = Assert.Single(h.Discord.Requests);
        Assert.Equal(Hook, url.ToString());
        var embed = JsonDocument.Parse(body).RootElement.GetProperty("embeds")[0];
        Assert.DoesNotContain("Appartement", embed.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Appartement", embed.GetProperty("title").GetString(), StringComparison.Ordinal);
        Assert.Equal("sent", Assert.Single(await HouseholdAsync(h)).State);
    }

    [Fact]
    public async Task Household_posts_once_per_key()
    {
        await using var h = await StartAsync();
        h.Source.Signals.Add(SignalOf(Action, "a:1", await SharedAsync(h)));
        await EvaluateAllAsync(h);
        Guid bobRow;
        await using (var db = PostgresFixture.NewPlatformContext(h.App.ConnectionString))
            bobRow = (await db.Notifications.AsNoTracking().SingleAsync(n => n.PersonId == h.App.AdultB.Value && n.Key == "a:1")).Id;
        await using (var scope = h.App.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<INotificationStore>().SnoozeAsync(bobRow, new LocalDate(2025, 10, 20), default);

        h.Clock.Now = Instant.FromUtc(2025, 10, 21, 8, 0);
        await EvaluateAllAsync(h);

        await using (var db = PostgresFixture.NewPlatformContext(h.App.ConnectionString))
        {
            var woke = await db.Notifications.AsNoTracking().SingleAsync(n => n.Id == bobRow);
            Assert.Equal((NotificationStates.Open, 2), (woke.State, woke.Round));
        }

        Assert.Single(await HouseholdAsync(h));
    }

    [Fact]
    public async Task A_purged_first_opener_does_not_free_the_key()
    {
        await using var h = await StartAsync();
        h.Source.Signals.Add(SignalOf(Action, "a:1", await SharedAsync(h)));
        await EvaluateAllAsync(h);
        await DispatchAsync(h);
        Guid aliceRow;
        await using (var db = PostgresFixture.NewPlatformContext(h.App.ConnectionString))
            aliceRow = (await db.Notifications.AsNoTracking().SingleAsync(n => n.PersonId == h.App.AdultA.Value && n.Key == "a:1")).Id;
        await using (var scope = h.App.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<INotificationStore>().ApplyAsync(h.App.AdultA, [new LifecycleChange.Resolve(aliceRow)], default);

        h.Clock.Now = Instant.FromUtc(2026, 2, 1, 8, 0);
        await using (var scope = h.App.Services.CreateAsyncScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<INotificationStore>().PurgeAsync(h.Clock.Now - NodaTime.Duration.FromDays(90), scope.ServiceProvider.GetRequiredService<NotificationCatalog>().Kinds.Keys.ToList(), default) > 0);
        await EvaluateAllAsync(h);
        await DispatchAsync(h);

        var delivery = Assert.Single(await HouseholdAsync(h));
        Assert.Null(delivery.NotificationId);
        Assert.Equal("sent", delivery.State);
        Assert.Single(h.Discord.Requests);
    }

    [Fact]
    public async Task Private_or_partly_shared_items_never_reach_the_household()
    {
        await using var h = await StartAsync();
        var mine = await h.App.NewSubscriptionAsync("Mien", h.App.AdultA, Visibility.Private);
        var his = await h.App.NewSubscriptionAsync("Sien", h.App.AdultB, Visibility.Private);
        var shared = await SharedAsync(h);
        h.Source.Signals.Add(SignalOf(Action, "a:1", mine));
        h.Source.Signals.Add(SignalOf(Action, "a:2", his));
        h.Source.Signals.Add(SignalOf(Action, "a:3", shared, mine));

        await EvaluateAllAsync(h);

        Assert.Empty(await HouseholdAsync(h));
    }

    [Fact]
    public async Task Info_and_item_less_rows_never_reach_the_household()
    {
        await using var h = await StartAsync();
        h.Source.Signals.Add(SignalOf(Info, "i:1", await SharedAsync(h)));
        h.Source.Signals.Add(SignalOf(Routine, "routine_due:1"));

        await EvaluateAllAsync(h);

        Assert.Empty(await HouseholdAsync(h));
    }

    [Fact]
    public async Task Household_rechecks_every_adult_at_send()
    {
        await using var h = await StartAsync();
        var item = await SharedAsync(h);
        h.Source.Signals.Add(SignalOf(Critical, "c:1", item));
        await EvaluateAllAsync(h);
        Assert.Single(await HouseholdAsync(h));

        await SetVisibilityAsync(h, item, "private");
        await DispatchAsync(h);

        Assert.Empty(h.Discord.Requests);
        Assert.Equal("skipped", Assert.Single(await HouseholdAsync(h)).State);
    }

    [Fact]
    public async Task Broken_household_webhook()
    {
        await using var h = await StartAsync();
        h.Discord.Answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        h.Source.Signals.Add(SignalOf(Action, "a:1", await SharedAsync(h)));
        await EvaluateAllAsync(h);

        await DispatchAsync(h);

        var delivery = Assert.Single(await HouseholdAsync(h));
        Assert.Equal(("failed", "notifications.channel_broken 404"), (delivery.State, delivery.LastError));
        var admin = await h.App.GetAsync<JsonElement>(h.App.AdultA, AdminRoute);
        Assert.Equal("broken", admin.GetProperty("discord").GetProperty("status").GetString());

        h.Source.Signals.Add(SignalOf(Action, "a:2", await SharedAsync(h, "Autre")));
        await EvaluateAllAsync(h);
        Assert.Single(await HouseholdAsync(h));
    }

    [Fact]
    public async Task Admin_test_on_the_household_webhook()
    {
        await using var h = await StartAsync();

        var sent = await h.App.RawAsync(h.App.AdultA, HttpMethod.Post, AdminRoute + "/test", new { channel = "discord_household" });

        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.True((await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sent").GetBoolean());
        Assert.Equal(Hook, Assert.Single(h.Discord.Requests).Url.ToString());

        await using var bare = await StartAsync(webhook: false);
        var none = await bare.App.RawAsync(bare.App.AdultA, HttpMethod.Post, AdminRoute + "/test", new { channel = "discord_household" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode);
        var error = Assert.Single((await none.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").EnumerateArray());
        Assert.Equal(("channel", "notifications.discord_unavailable"), (error.GetProperty("path").GetString(), error.GetProperty("code").GetString()));

        var personal = await h.App.RawAsync(h.App.AdultA, HttpMethod.Post, AdminRoute + "/test", new { channel = "discord_person" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, personal.StatusCode);
    }
}
