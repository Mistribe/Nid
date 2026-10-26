using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Platform.Infrastructure.Persistence;
using Nid.Platform.Notifications;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Subscriptions.IntegrationTests.Notifications;

/// <summary>Notifications design §5.2: the digest.</summary>
public class DigestTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string PreferencesRoute = "/api/v1/platform/notifications/preferences";

    private static readonly NotificationKind Action = new("fake_action", "finding", NotificationLevel.Action, "fake", null);
    private static readonly NotificationKind Info = new("fake_info", "finding", NotificationLevel.Info, "fake", null);

    // Household zone: Europe/Paris, UTC+2 until 2025-10-26. 2025-10-15 is a Wednesday.
    private static readonly Instant Wednesday10h = Instant.FromUtc(2025, 10, 15, 8, 0);

    private sealed class SettableClock(Instant now) : IClock
    {
        public Instant Now { get; set; } = now;

        public Instant GetCurrentInstant() => Now;
    }

    private sealed class SmtpConfigured : IEmailAvailability
    {
        public bool IsConfigured => true;
    }

    private sealed class FakeSource : INotificationSource
    {
        public string Name => "fake";

        public IReadOnlyList<NotificationKind> Kinds { get; } = [Action, Info];

        public List<Signal> Signals { get; } = [];

        public Task<Result<IReadOnlyList<Signal>>> SignalsAsync(PersonId reader, LocalDate today, LocalDate horizon, CancellationToken ct)
            => Task.FromResult(Result.Success<IReadOnlyList<Signal>>(Signals.ToList()));
    }

    private sealed class FakeChannel : INotificationChannel
    {
        public string Name => "email";

        public bool IsAvailable => true;

        public List<(RenderedMessage Message, ChannelTarget Target)> Sent { get; } = [];

        public Task<Result> SendAsync(RenderedMessage message, ChannelTarget target, CancellationToken ct)
        {
            Sent.Add((message, target));
            return Task.FromResult(Result.Success());
        }
    }

    private sealed record Harness(NidApp App, FakeSource Source, FakeChannel Channel, SettableClock Clock) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private async Task<Harness> StartAsync(
        DigestFrequency frequency = DigestFrequency.Daily, int weekday = 1, InfoMode info = InfoMode.Digest, Instant? at = null,
        string action = "digest")
    {
        var source = new FakeSource();
        var channel = new FakeChannel();
        var clock = new SettableClock(at ?? Wednesday10h);
        var app = await NidApp.StartAsync(fixture, services =>
        {
            services.AddSingleton<IClock>(clock);
            services.RemoveAll<INotificationSource>();
            services.AddSingleton<INotificationSource>(source);
            services.AddSingleton<INotificationChannel>(channel);
            services.RemoveAll<IEmailAvailability>();
            services.AddSingleton<IEmailAvailability, SmtpConfigured>();
        });
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
        {
            var row = await core.Persons.SingleAsync(p => p.Id == app.AdultA.Value);
            row.Email = "a@example.test";
            await core.SaveChangesAsync();
        }

        var current = await app.GetAsync<JsonElement>(app.AdultA, PreferencesRoute);
        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, PreferencesRoute, new
        {
            emailEnabled = true, action, info = info.ToString().ToLowerInvariant(),
            frequency = frequency.ToString().ToLowerInvariant(), weekday, leadDays = new Dictionary<string, int>(),
            version = current.GetProperty("version").GetInt32(),
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return new Harness(app, source, channel, clock);
    }

    private static Signal SignalOf(NotificationKind kind, string key, ItemId item)
        => new(key, kind.Kind, [item], null, new Dictionary<string, string>());

    private static async Task EvaluateAsync(Harness h)
    {
        var (_, failure) = await h.App.InvokeAsync(h.App.AdultA, new EvaluateNotificationsCommand(h.App.AdultA, true));
        Assert.Null(failure);
    }

    private static async Task<int> DigestAsync(Harness h)
    {
        var (response, failure) = await h.App.InvokeAsync(h.App.AdultA, new SendDigestsCommand());
        Assert.Null(failure);
        return ((DigestSummary)response!).Queued;
    }

    private static async Task<DispatchSummary> DispatchAsync(Harness h)
    {
        var (response, failure) = await h.App.InvokeAsync(h.App.AdultA, new DispatchNotificationsCommand());
        Assert.Null(failure);
        return (DispatchSummary)response!;
    }

    private static async Task<List<NotificationRow>> RowsAsync(Harness h)
    {
        await using var db = PostgresFixture.NewPlatformContext(h.App.ConnectionString);
        return await db.Notifications.AsNoTracking().OrderBy(n => n.Key).ToListAsync();
    }

    private static async Task<(List<NotificationDigestRow> Digests, List<NotificationDeliveryRow> Deliveries)> OutboxAsync(Harness h)
    {
        await using var db = PostgresFixture.NewPlatformContext(h.App.ConnectionString);
        return (await db.Digests.AsNoTracking().ToListAsync(), await db.Deliveries.AsNoTracking().ToListAsync());
    }

    private static Task<ItemId> ItemAsync(Harness h, string name = "Netflix")
        => h.App.NewSubscriptionAsync(name, h.App.AdultA, Visibility.Shared);

    private static void AddThree(Harness h, ItemId item)
    {
        h.Source.Signals.Add(SignalOf(Info, "i:1", item));
        h.Source.Signals.Add(SignalOf(Info, "i:2", item));
        h.Source.Signals.Add(SignalOf(Action, "a:1", item));
    }

    [Fact]
    public async Task Daily_digest_groups_the_period()
    {
        await using var h = await StartAsync();
        AddThree(h, await ItemAsync(h));

        await EvaluateAsync(h);
        Assert.Empty((await OutboxAsync(h)).Deliveries); // digest mode: nothing queued at once

        Assert.Equal(1, await DigestAsync(h));
        await DispatchAsync(h);

        var (message, target) = Assert.Single(h.Channel.Sent);
        Assert.Equal("Nid : 3 notifications", message.Subject);
        Assert.Equal(3, message.Text.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal)));
        Assert.Equal("a@example.test", target.Address);
        var rows = await RowsAsync(h);
        Assert.All(rows, r => Assert.NotNull(r.DigestedAt));
        var (digests, deliveries) = await OutboxAsync(h);
        var digest = Assert.Single(digests);
        var delivery = Assert.Single(deliveries);
        Assert.Equal((digest.Id, "sent", null), (delivery.DigestId, delivery.State, delivery.NotificationId));
        Assert.All(rows, r => Assert.Equal(digest.Id, r.DigestId));
    }

    [Fact]
    public async Task Weekly_digest_waits_for_its_day_and_hour()
    {
        // Monday 2025-10-13; the household's clock is two hours ahead of UTC.
        await using var h = await StartAsync(DigestFrequency.Weekly, weekday: 1, at: Instant.FromUtc(2025, 10, 12, 8, 0));
        AddThree(h, await ItemAsync(h));
        await EvaluateAsync(h);

        Assert.Equal(0, await DigestAsync(h)); // Sunday 10:00

        h.Clock.Now = Instant.FromUtc(2025, 10, 13, 5, 0); // Monday 07:00
        Assert.Equal(0, await DigestAsync(h));

        h.Clock.Now = Instant.FromUtc(2025, 10, 13, 6, 10); // Monday 08:10
        Assert.Equal(1, await DigestAsync(h));
        await DispatchAsync(h);

        Assert.Single(h.Channel.Sent);
        var (digests, _) = await OutboxAsync(h);
        Assert.Equal("2025-W42", Assert.Single(digests).PeriodKey);
    }

    [Fact]
    public async Task One_digest_per_period()
    {
        await using var h = await StartAsync();
        var item = await ItemAsync(h);
        AddThree(h, item);
        await EvaluateAsync(h);

        Assert.Equal(1, await DigestAsync(h));
        h.Source.Signals.Add(SignalOf(Info, "i:3", item)); // opens after the digest, the same morning
        await EvaluateAsync(h);
        Assert.Equal(0, await DigestAsync(h));

        var (digests, deliveries) = await OutboxAsync(h);
        var digest = Assert.Single(digests);
        Assert.Equal("2025-10-15", digest.PeriodKey);
        Assert.Single(deliveries);
        Assert.Null((await RowsAsync(h)).Single(r => r.Key == "i:3").DigestedAt); // waits for the next period
    }

    [Fact]
    public async Task Nothing_to_say_sends_nothing()
    {
        await using var h = await StartAsync();

        Assert.Equal(0, await DigestAsync(h));
        await DispatchAsync(h);

        Assert.Empty(h.Channel.Sent);
        var (digests, deliveries) = await OutboxAsync(h);
        Assert.Empty(digests);
        Assert.Empty(deliveries);

        // No digest row was kept, so a row that opens later the same day still goes out.
        h.Source.Signals.Add(SignalOf(Action, "a:1", await ItemAsync(h)));
        await EvaluateAsync(h);
        Assert.Equal(1, await DigestAsync(h));
    }

    [Fact]
    public async Task A_row_never_goes_in_two_digests()
    {
        await using var h = await StartAsync();
        AddThree(h, await ItemAsync(h));
        await EvaluateAsync(h);
        Assert.Equal(1, await DigestAsync(h));

        h.Clock.Now += NodaTime.Duration.FromDays(1);
        await EvaluateAsync(h);
        Assert.Equal(0, await DigestAsync(h));
        await DispatchAsync(h);

        Assert.Single(h.Channel.Sent);
        Assert.Single((await OutboxAsync(h)).Digests);
    }

    [Fact]
    public async Task Centre_only_info_never_digested()
    {
        await using var h = await StartAsync(info: InfoMode.Centre);
        var item = await ItemAsync(h);
        h.Source.Signals.Add(SignalOf(Info, "i:1", item));
        await EvaluateAsync(h);

        Assert.Equal(0, await DigestAsync(h));

        Assert.Null((await RowsAsync(h)).Single().DigestedAt);
        var (digests, deliveries) = await OutboxAsync(h);
        Assert.Empty(digests);
        Assert.Empty(deliveries);
    }

    [Fact]
    public async Task A_digest_whose_rows_are_all_gone_is_skipped()
    {
        await using var h = await StartAsync();
        AddThree(h, await ItemAsync(h));
        await EvaluateAsync(h);
        Assert.Equal(1, await DigestAsync(h));

        h.Source.Signals.Clear(); // the situations are over: the lifecycle resolves the rows
        await EvaluateAsync(h);
        var summary = await DispatchAsync(h);

        Assert.Equal((0, 1), (summary.Sent, summary.Skipped));
        Assert.Empty(h.Channel.Sent);
        Assert.Equal("skipped", Assert.Single((await OutboxAsync(h)).Deliveries).State);
    }

    [Fact]
    public async Task A_woken_row_reaches_the_next_digest_and_leaves_the_old_one()
    {
        await using var h = await StartAsync();
        var item = await ItemAsync(h);
        h.Source.Signals.Add(SignalOf(Action, "a:1", item));
        h.Source.Signals.Add(SignalOf(Info, "i:1", item));
        await EvaluateAsync(h);
        Assert.Equal(1, await DigestAsync(h)); // period 1, not dispatched yet
        var id = (await RowsAsync(h)).Single(r => r.Key == "a:1").Id;
        await using (var scope = h.App.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<INotificationStore>().SnoozeAsync(id, new LocalDate(2025, 10, 16), default);

        h.Clock.Now += NodaTime.Duration.FromDays(1);
        await EvaluateAsync(h); // wakes: round 2
        var woken = (await RowsAsync(h)).Single(r => r.Key == "a:1");
        Assert.Equal((2, null), (woken.Round, woken.DigestedAt));
        Assert.Null(woken.DigestId);
        await DispatchAsync(h); // period 1's message no longer carries the woken row
        Assert.Equal("Nid : 1 notification", Assert.Single(h.Channel.Sent).Message.Subject);

        Assert.Equal(1, await DigestAsync(h)); // period 2
        await DispatchAsync(h);
        Assert.Equal(2, h.Channel.Sent.Count);
        Assert.Equal("Nid : 1 notification", h.Channel.Sent[1].Message.Subject);
        Assert.Equal(2, (await OutboxAsync(h)).Digests.Count);
    }

    [Fact]
    public async Task A_reopened_row_reaches_the_next_digest_and_leaves_the_old_one()
    {
        await using var h = await StartAsync();
        var item = await ItemAsync(h);
        h.Source.Signals.Add(SignalOf(Action, "a:1", item));
        h.Source.Signals.Add(SignalOf(Info, "i:1", item));
        await EvaluateAsync(h);
        Assert.Equal(1, await DigestAsync(h)); // period 1, not dispatched yet

        h.Clock.Now += NodaTime.Duration.FromDays(1);
        h.Source.Signals.RemoveAll(s => s.Key == "a:1");
        await EvaluateAsync(h); // resolved
        h.Clock.Now += NodaTime.Duration.FromDays(1);
        h.Source.Signals.Add(SignalOf(Action, "a:1", item));
        await EvaluateAsync(h); // reopened: round 2
        var reopened = (await RowsAsync(h)).Single(r => r.Key == "a:1");
        Assert.Equal(2, reopened.Round);
        Assert.Null(reopened.DigestedAt);
        Assert.Null(reopened.DigestId);
        await DispatchAsync(h);
        Assert.Equal("Nid : 1 notification", Assert.Single(h.Channel.Sent).Message.Subject);

        Assert.Equal(1, await DigestAsync(h));
        await DispatchAsync(h);
        Assert.Equal(2, h.Channel.Sent.Count);
    }

    private static async Task SwitchActionAsync(Harness h, string action)
    {
        var current = await h.App.GetAsync<JsonElement>(h.App.AdultA, PreferencesRoute);
        var response = await h.App.RawAsync(h.App.AdultA, HttpMethod.Put, PreferencesRoute, new
        {
            emailEnabled = true, action, info = "digest", frequency = "daily", weekday = 1, leadDays = new Dictionary<string, int>(),
            version = current.GetProperty("version").GetInt32(),
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Switching_to_the_digest_does_not_send_an_action_row_twice()
    {
        await using var h = await StartAsync(action: "immediate");
        var item = await ItemAsync(h);
        h.Source.Signals.Add(SignalOf(Action, "a:1", item));
        h.Source.Signals.Add(SignalOf(Info, "i:1", item));
        await EvaluateAsync(h);
        Assert.Equal(1, (await DispatchAsync(h)).Sent); // the action row went out at once
        await SwitchActionAsync(h, "digest");

        Assert.Equal(1, await DigestAsync(h));
        await DispatchAsync(h);

        Assert.Equal(2, h.Channel.Sent.Count);
        Assert.Equal("Nid : 1 notification", h.Channel.Sent[1].Message.Subject); // the info row only
        var rows = await RowsAsync(h);
        Assert.Null(rows.Single(r => r.Key == "a:1").DigestedAt);
        Assert.NotNull(rows.Single(r => r.Key == "i:1").DigestedAt);
    }

    [Fact]
    public async Task Switching_to_immediate_leaves_the_waiting_action_row_to_the_digest()
    {
        await using var h = await StartAsync();
        h.Source.Signals.Add(SignalOf(Action, "a:1", await ItemAsync(h)));
        await EvaluateAsync(h);
        Assert.Empty((await OutboxAsync(h)).Deliveries); // it waits for the digest
        await SwitchActionAsync(h, "immediate");
        await EvaluateAsync(h);
        Assert.Empty((await OutboxAsync(h)).Deliveries); // not opened again: nothing sends it at once

        Assert.Equal(1, await DigestAsync(h));
        await DispatchAsync(h);

        Assert.Equal("Nid : 1 notification", Assert.Single(h.Channel.Sent).Message.Subject);
        Assert.Single((await OutboxAsync(h)).Deliveries);
    }

    [Fact]
    public void The_weekly_key_follows_the_iso_year()
    {
        Assert.Equal("2025-W01", SendDigestsHandler.PeriodKey(DigestFrequency.Weekly, new LocalDate(2024, 12, 30)));
        Assert.Equal("2024-12-30", SendDigestsHandler.PeriodKey(DigestFrequency.Daily, new LocalDate(2024, 12, 30)));
    }
}
