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
using UnambitiousFx.Functional.Failures;

namespace Nid.Subscriptions.IntegrationTests.Notifications;

/// <summary>Notifications design §5.1: the outbox and the dispatcher.</summary>
public class DispatchTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string PreferencesRoute = "/api/v1/platform/notifications/preferences";

    private static readonly NotificationKind Action = new("fake_action", "finding", NotificationLevel.Action, "fake", null);
    private static readonly NotificationKind Info = new("fake_info", "finding", NotificationLevel.Info, "fake", null);
    private static readonly NotificationKind Critical = new("fake_critical", "finding", NotificationLevel.Critical, "fake", null);

    // Household zone: Europe/Paris, UTC+2 until 2025-10-26.
    private static readonly Instant Start = Instant.FromUtc(2025, 10, 15, 8, 0);

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

        public IReadOnlyList<NotificationKind> Kinds { get; } = [Action, Info, Critical];

        public List<Signal> Signals { get; } = [];

        public Task<Result<IReadOnlyList<Signal>>> SignalsAsync(PersonId reader, LocalDate today, LocalDate horizon, CancellationToken ct)
            => Task.FromResult(Result.Success<IReadOnlyList<Signal>>(Signals.ToList()));
    }

    private sealed class FakeChannel : INotificationChannel
    {
        public string Name => "email";

        public bool IsAvailable { get; set; } = true;

        public List<(RenderedMessage Message, ChannelTarget Target)> Sent { get; } = [];

        /// <summary>The next results, one per call: "" succeeds, null throws, anything else fails with that code. Empty succeeds.</summary>
        public Queue<string?> Script { get; } = [];

        public Task<Result> SendAsync(RenderedMessage message, ChannelTarget target, CancellationToken ct)
        {
            if (Script.Count > 0)
            {
                var next = Script.Dequeue();
                if (next is null)
                    throw new InvalidOperationException("The channel exploded.");
                if (next != "")
                    return Task.FromResult(Result.Failure(new Failure(next, "failed", null)));
            }

            Sent.Add((message, target));
            return Task.FromResult(Result.Success());
        }
    }

    private sealed record Harness(NidApp App, FakeSource Source, FakeChannel Channel, SettableClock Clock) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private async Task<Harness> StartAsync(bool emailOn = true, ActionMode action = ActionMode.Immediate, Action<IServiceCollection>? more = null)
    {
        var source = new FakeSource();
        var channel = new FakeChannel();
        var clock = new SettableClock(Start);
        var app = await NidApp.StartAsync(fixture, services =>
        {
            services.AddSingleton<IClock>(clock);
            services.RemoveAll<INotificationSource>();
            services.AddSingleton<INotificationSource>(source);
            services.AddSingleton<INotificationChannel>(channel);
            services.RemoveAll<IEmailAvailability>();
            services.AddSingleton<IEmailAvailability, SmtpConfigured>();
            more?.Invoke(services);
        });
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
        {
            var row = await core.Persons.SingleAsync(p => p.Id == app.AdultA.Value);
            row.Email = "a@example.test";
            await core.SaveChangesAsync();
        }

        var h = new Harness(app, source, channel, clock);
        if (emailOn)
            await SetPreferencesAsync(h, emailEnabled: true, action);
        return h;
    }

    private static async Task SetPreferencesAsync(Harness h, bool emailEnabled, ActionMode action = ActionMode.Immediate)
    {
        var current = await h.App.GetAsync<JsonElement>(h.App.AdultA, PreferencesRoute);
        var response = await h.App.RawAsync(h.App.AdultA, HttpMethod.Put, PreferencesRoute, new
        {
            emailEnabled, action = action.ToString().ToLowerInvariant(), info = "digest", frequency = "weekly", weekday = 1,
            leadDays = new Dictionary<string, int>(), version = current.GetProperty("version").GetInt32(),
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static Signal SignalOf(NotificationKind kind, string key, ItemId item)
        => new(key, kind.Kind, [item], null, new Dictionary<string, string>());

    private static async Task EvaluateAsync(Harness h)
    {
        var (_, failure) = await h.App.InvokeAsync(h.App.AdultA, new EvaluateNotificationsCommand(h.App.AdultA, true));
        Assert.Null(failure);
    }

    private static async Task<DispatchSummary> DispatchAsync(Harness h)
    {
        var (response, failure) = await h.App.InvokeAsync(h.App.AdultA, new DispatchNotificationsCommand());
        Assert.Null(failure);
        return (DispatchSummary)response!;
    }

    private static async Task<List<NotificationDeliveryRow>> DeliveriesAsync(Harness h)
    {
        await using var db = PostgresFixture.NewPlatformContext(h.App.ConnectionString);
        return await db.Deliveries.AsNoTracking().OrderBy(d => d.CreatedAt).ThenBy(d => d.Round).ThenBy(d => d.Id).ToListAsync();
    }

    private static async Task<NotificationRow> RowAsync(Harness h, string key)
    {
        await using var db = PostgresFixture.NewPlatformContext(h.App.ConnectionString);
        return await db.Notifications.AsNoTracking().SingleAsync(n => n.PersonId == h.App.AdultA.Value && n.Key == key);
    }

    private static async Task SetVisibilityAsync(NidApp app, ItemId item, string visibility)
    {
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var row = await core.Items.SingleAsync(i => i.Id == item.Value);
        row.Visibility = visibility;
        await core.SaveChangesAsync();
    }

    private static Task<ItemId> ItemAsync(Harness h, string name = "Netflix", PersonId? owner = null)
        => h.App.NewSubscriptionAsync(name, owner ?? h.App.AdultA, Visibility.Shared);

    [Fact]
    public async Task Opening_an_action_row_sends_it_once()
    {
        await using var h = await StartAsync();
        h.Source.Signals.Add(SignalOf(Action, "a:1", await ItemAsync(h)));

        await EvaluateAsync(h);
        var first = await DispatchAsync(h);
        var second = await DispatchAsync(h);

        var (message, target) = Assert.Single(h.Channel.Sent);
        Assert.Equal((h.App.AdultA, "a@example.test"), (target.Person, target.Address));
        Assert.False(string.IsNullOrWhiteSpace(message.Subject));
        Assert.Equal(1, first.Sent);
        Assert.Equal(0, second.Sent);
        var delivery = Assert.Single(await DeliveriesAsync(h));
        Assert.Equal(("sent", "email", 1, 1), (delivery.State, delivery.Channel, delivery.Round, delivery.Attempts));
        Assert.NotNull(delivery.SentAt);
    }

    [Fact]
    public async Task Critical_rows_go_out_even_when_actions_are_in_the_digest()
    {
        await using var h = await StartAsync(action: ActionMode.Digest);
        var item = await ItemAsync(h);
        h.Source.Signals.Add(SignalOf(Critical, "c:1", item));
        h.Source.Signals.Add(SignalOf(Action, "a:1", item));

        await EvaluateAsync(h);
        await DispatchAsync(h);

        Assert.Single(h.Channel.Sent);
        Assert.Single(await DeliveriesAsync(h));
    }

    [Fact]
    public async Task Info_rows_and_digest_mode_enqueue_nothing()
    {
        await using var h = await StartAsync(action: ActionMode.Digest);
        var item = await ItemAsync(h);
        h.Source.Signals.Add(SignalOf(Info, "i:1", item));
        h.Source.Signals.Add(SignalOf(Action, "a:1", item));

        await EvaluateAsync(h);
        await DispatchAsync(h);

        Assert.Empty(await DeliveriesAsync(h));
        Assert.Empty(h.Channel.Sent);
    }

    [Fact]
    public async Task Nothing_is_enqueued_for_an_unavailable_channel_or_a_person_without_an_address()
    {
        await using var h = await StartAsync();
        h.Channel.IsAvailable = false;
        h.Source.Signals.Add(SignalOf(Action, "a:1", await ItemAsync(h)));

        await EvaluateAsync(h);
        Assert.Empty(await DeliveriesAsync(h));

        await using (var core = PostgresFixture.NewCoreContext(h.App.ConnectionString))
        {
            var row = await core.Persons.SingleAsync(p => p.Id == h.App.AdultA.Value);
            row.Email = null;
            await core.SaveChangesAsync();
        }

        h.Channel.IsAvailable = true;
        h.Source.Signals.Add(SignalOf(Action, "a:2", await ItemAsync(h, "Disney")));
        await EvaluateAsync(h);
        Assert.Empty(await DeliveriesAsync(h));
    }

    [Theory]
    [InlineData("resolved")]
    [InlineData("dismissed")]
    [InlineData("snoozed")]
    [InlineData("private")]
    [InlineData("email_off")]
    [InlineData("channel_off")]
    public async Task Dispatcher_skips_what_is_no_longer_sendable(string change)
    {
        await using var h = await StartAsync();
        // Owned by the other adult and shared, so that making it private hides it from the recipient.
        var item = await ItemAsync(h, owner: h.App.AdultB);
        h.Source.Signals.Add(SignalOf(Action, "a:1", item));
        await EvaluateAsync(h);
        var id = (await RowAsync(h, "a:1")).Id;
        Assert.Single(await DeliveriesAsync(h));

        await using (var scope = h.App.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<INotificationStore>();
            switch (change)
            {
                case "resolved":
                    await store.ApplyAsync(h.App.AdultA, [new LifecycleChange.Resolve(id)], default);
                    break;
                case "dismissed":
                    await store.DismissAsync(id, Start, default);
                    break;
                case "snoozed":
                    await store.SnoozeAsync(id, new LocalDate(2025, 11, 15), default);
                    break;
            }
        }

        switch (change)
        {
            case "private":
                await SetVisibilityAsync(h.App, item, "private");
                break;
            case "email_off":
                await SetPreferencesAsync(h, emailEnabled: false);
                break;
            case "channel_off":
                h.Channel.IsAvailable = false;
                break;
        }

        var summary = await DispatchAsync(h);

        Assert.Empty(h.Channel.Sent);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal("skipped", Assert.Single(await DeliveriesAsync(h)).State);
    }

    [Fact]
    public async Task Retries_then_fails_without_duplicates()
    {
        await using var h = await StartAsync();
        h.Source.Signals.Add(SignalOf(Action, "a:1", await ItemAsync(h)));
        for (var i = 0; i < 6; i++)
            h.Channel.Script.Enqueue(NotificationChannelCodes.Unreachable);
        await EvaluateAsync(h);

        var first = await DispatchAsync(h);
        Assert.Equal(1, first.Retried);
        foreach (var minutes in new[] { 1, 5, 30, 120, 720 })
        {
            var delivery = Assert.Single(await DeliveriesAsync(h));
            Assert.Equal("pending", delivery.State);
            Assert.Equal(h.Clock.Now + NodaTime.Duration.FromMinutes(minutes), delivery.NextAttemptAt);

            h.Clock.Now += NodaTime.Duration.FromMinutes(minutes) - NodaTime.Duration.FromSeconds(1);
            Assert.Equal(0, (await DispatchAsync(h)).Retried); // not due yet
            h.Clock.Now += NodaTime.Duration.FromSeconds(1);
            await DispatchAsync(h);
        }

        var failed = Assert.Single(await DeliveriesAsync(h));
        Assert.Equal(("failed", 6, NotificationChannelCodes.Unreachable), (failed.State, failed.Attempts, failed.LastError));
        Assert.Null(failed.NextAttemptAt);

        // The server is back: nothing is sent for what already failed.
        h.Clock.Now += NodaTime.Duration.FromDays(2);
        await DispatchAsync(h);
        Assert.Empty(h.Channel.Sent);
    }

    [Fact]
    public async Task A_retry_that_succeeds_sends_once()
    {
        await using var h = await StartAsync();
        h.Source.Signals.Add(SignalOf(Action, "a:1", await ItemAsync(h)));
        h.Channel.Script.Enqueue(NotificationChannelCodes.Unreachable);
        await EvaluateAsync(h);

        await DispatchAsync(h);
        h.Clock.Now += NodaTime.Duration.FromMinutes(1);
        var summary = await DispatchAsync(h);
        await DispatchAsync(h);

        Assert.Equal(1, summary.Sent);
        Assert.Single(h.Channel.Sent);
        var delivery = Assert.Single(await DeliveriesAsync(h));
        Assert.Equal(("sent", 2), (delivery.State, delivery.Attempts));
    }

    [Fact]
    public async Task Rejected_fails_at_once()
    {
        await using var h = await StartAsync();
        h.Source.Signals.Add(SignalOf(Action, "a:1", await ItemAsync(h)));
        h.Channel.Script.Enqueue(NotificationChannelCodes.Rejected);
        await EvaluateAsync(h);

        var summary = await DispatchAsync(h);
        h.Clock.Now += NodaTime.Duration.FromDays(1);
        await DispatchAsync(h);

        Assert.Equal(1, summary.Failed);
        Assert.Empty(h.Channel.Sent);
        var delivery = Assert.Single(await DeliveriesAsync(h));
        Assert.Equal(("failed", 1, NotificationChannelCodes.Rejected), (delivery.State, delivery.Attempts, delivery.LastError));
        Assert.Null(delivery.NextAttemptAt);
    }

    [Fact]
    public async Task Wake_sends_a_second_round()
    {
        await using var h = await StartAsync();
        h.Source.Signals.Add(SignalOf(Action, "a:1", await ItemAsync(h)));
        await EvaluateAsync(h);
        await DispatchAsync(h);
        var id = (await RowAsync(h, "a:1")).Id;
        await using (var scope = h.App.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<INotificationStore>().SnoozeAsync(id, new LocalDate(2025, 10, 20), default);

        h.Clock.Now = Instant.FromUtc(2025, 10, 21, 8, 0);
        await EvaluateAsync(h);
        await DispatchAsync(h);
        await DispatchAsync(h);

        Assert.Equal(2, h.Channel.Sent.Count);
        Assert.Equal(2, (await RowAsync(h, "a:1")).Round);
        Assert.Equal([1, 2], (await DeliveriesAsync(h)).Select(d => d.Round));
        Assert.All(await DeliveriesAsync(h), d => Assert.Equal("sent", d.State));
    }

    [Fact]
    public async Task A_resolved_key_signalled_again_is_a_new_round()
    {
        await using var h = await StartAsync();
        var signal = SignalOf(Action, "a:1", await ItemAsync(h));
        h.Source.Signals.Add(signal);
        await EvaluateAsync(h);
        await DispatchAsync(h);

        h.Source.Signals.Clear();
        h.Clock.Now += NodaTime.Duration.FromDays(1);
        await EvaluateAsync(h);
        h.Source.Signals.Add(signal);
        h.Clock.Now += NodaTime.Duration.FromDays(1);
        await EvaluateAsync(h);
        await DispatchAsync(h);

        Assert.Equal(2, h.Channel.Sent.Count);
        Assert.Equal([1, 2], (await DeliveriesAsync(h)).Select(d => d.Round));
    }

    [Fact]
    public async Task Turning_email_on_does_not_send_the_backlog()
    {
        await using var h = await StartAsync(emailOn: false);
        var item = await ItemAsync(h);
        h.Source.Signals.Add(SignalOf(Action, "a:1", item));
        await EvaluateAsync(h);
        Assert.Empty(await DeliveriesAsync(h));

        await SetPreferencesAsync(h, emailEnabled: true);
        h.Clock.Now += NodaTime.Duration.FromHours(1);
        await EvaluateAsync(h);
        await DispatchAsync(h);
        Assert.Empty(h.Channel.Sent);

        h.Source.Signals.Add(SignalOf(Action, "a:2", item));
        h.Clock.Now += NodaTime.Duration.FromHours(1);
        await EvaluateAsync(h);
        await DispatchAsync(h);

        var (_, target) = Assert.Single(h.Channel.Sent);
        Assert.Equal(h.App.AdultA, target.Person);
        var delivery = Assert.Single(await DeliveriesAsync(h));
        Assert.Equal((await RowAsync(h, "a:2")).Id, delivery.NotificationId);
    }

    [Fact]
    public async Task A_failing_send_does_not_stop_the_batch()
    {
        await using var h = await StartAsync();
        var item = await ItemAsync(h);
        h.Source.Signals.Add(SignalOf(Action, "a:1", item));
        h.Source.Signals.Add(SignalOf(Action, "a:2", item));
        h.Channel.Script.Enqueue(null); // the first call throws
        await EvaluateAsync(h);

        var summary = await DispatchAsync(h);

        Assert.Single(h.Channel.Sent);
        Assert.Equal((1, 1), (summary.Sent, summary.Retried));
        Assert.Equal(["pending", "sent"], (await DeliveriesAsync(h)).Select(d => d.State).OrderBy(s => s).ToList());
    }

    [Fact]
    public async Task The_batch_is_bounded()
    {
        await using var h = await StartAsync();
        var item = await ItemAsync(h);
        for (var i = 0; i < 53; i++)
            h.Source.Signals.Add(SignalOf(Action, $"a:{i}", item));
        await EvaluateAsync(h);

        var first = await DispatchAsync(h);
        var second = await DispatchAsync(h);

        Assert.Equal((50, 3), (first.Sent, second.Sent));
        Assert.Equal(53, h.Channel.Sent.Count);
    }

    private sealed class FlakyOutbox(IDeliveryOutbox inner, PersonId failFor, int[] failures) : IDeliveryOutbox
    {
        public async Task EnqueueAsync(PersonId person, IReadOnlyList<OpenedNotification> opened, NotificationPreferences prefs, CancellationToken ct)
        {
            if (person == failFor && failures[0]-- > 0)
                throw new InvalidOperationException("The outbox is down.");
            await inner.EnqueueAsync(person, opened, prefs, ct);
        }
    }

    [Fact]
    public async Task A_failed_enqueue_rolls_the_opening_back_and_the_next_run_redoes_it()
    {
        var failures = new[] { 1 };
        PersonId? a = null;
        await using var h = await StartAsync(more: services =>
            services.AddScoped<IDeliveryOutbox>(sp => new FlakyOutbox(ActivatorUtilities.CreateInstance<DeliveryOutbox>(sp), a!.Value, failures)));
        a = h.App.AdultA;
        h.Source.Signals.Add(SignalOf(Action, "a:1", await ItemAsync(h)));

        await EvaluateAsync(h);

        Assert.Empty(await RowsOfAsync(h));
        Assert.Empty(await DeliveriesAsync(h));
        await using (var db = PostgresFixture.NewPlatformContext(h.App.ConnectionString))
            Assert.False((await db.NotificationRuns.AsNoTracking().SingleAsync(r => r.PersonId == h.App.AdultA.Value)).Ok);

        await EvaluateAsync(h);

        Assert.Equal(NotificationStates.Open, Assert.Single(await RowsOfAsync(h)).State);
        Assert.Single(await DeliveriesAsync(h));
        await DispatchAsync(h);
        Assert.Single(h.Channel.Sent);
    }

    private static async Task<List<NotificationRow>> RowsOfAsync(Harness h)
    {
        await using var db = PostgresFixture.NewPlatformContext(h.App.ConnectionString);
        return await db.Notifications.AsNoTracking().Where(n => n.PersonId == h.App.AdultA.Value).ToListAsync();
    }

    private sealed class SettleThrowsStore(IDeliveryStore inner, Guid[] poisoned) : IDeliveryStore
    {
        public Task EnqueueAsync(IReadOnlyList<NewDelivery> deliveries, Instant now, CancellationToken ct) => inner.EnqueueAsync(deliveries, now, ct);

        public Task<IReadOnlyList<DeliveryRecord>> DueAsync(Instant now, int limit, CancellationToken ct) => inner.DueAsync(now, limit, ct);

        public Task<DeliveryRecord?> ClaimAsync(Guid id, Instant now, CancellationToken ct) => inner.ClaimAsync(id, now, ct);

        public Task SettleAsync(Guid id, string state, int attempts, Instant? nextAttemptAt, string? lastError, Instant? sentAt, CancellationToken ct)
            => state == DeliveryStates.Sent && id == poisoned[0]
                ? throw new InvalidOperationException("The database dropped the result.")
                : inner.SettleAsync(id, state, attempts, nextAttemptAt, lastError, sentAt, ct);

        public Task<IReadOnlyList<FailedDelivery>> FailedSinceAsync(Instant since, int limit, CancellationToken ct) => inner.FailedSinceAsync(since, limit, ct);
    }

    [Fact]
    public async Task A_delivery_that_always_crashes_ends_failed_and_does_not_starve_the_batch()
    {
        var poisoned = new Guid[1];
        await using var h = await StartAsync(more: services =>
            services.AddScoped<IDeliveryStore>(sp => new SettleThrowsStore(
                ActivatorUtilities.CreateInstance<Nid.Platform.Infrastructure.Notifications.DeliveryStore>(sp), poisoned)));
        var item = await ItemAsync(h);
        h.Source.Signals.Add(SignalOf(Action, "a:1", item));
        h.Source.Signals.Add(SignalOf(Action, "a:2", item));
        await EvaluateAsync(h);
        var bad = (await RowAsync(h, "a:1")).Id;
        poisoned[0] = (await DeliveriesAsync(h)).Single(d => d.NotificationId == bad).Id;

        foreach (var minutes in new[] { 0, 1, 5, 30, 120, 720 })
        {
            h.Clock.Now += NodaTime.Duration.FromMinutes(minutes);
            await DispatchAsync(h);
        }

        var deliveries = await DeliveriesAsync(h);
        var crashed = deliveries.Single(d => d.Id == poisoned[0]);
        Assert.Equal(("failed", 6, "notifications.dispatch_crashed"), (crashed.State, crashed.Attempts, crashed.LastError));
        Assert.Equal("sent", deliveries.Single(d => d.Id != poisoned[0]).State);

        var before = h.Channel.Sent.Count;
        h.Clock.Now += NodaTime.Duration.FromDays(2);
        await DispatchAsync(h);
        Assert.Equal(before, h.Channel.Sent.Count);
    }
}
