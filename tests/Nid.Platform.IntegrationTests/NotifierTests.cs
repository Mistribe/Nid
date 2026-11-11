using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.Platform;
using Nid.Platform.Contracts;
using Nid.Platform.Infrastructure.Persistence;
using Nid.Platform.Notifications;
using UnambitiousFx.Myelin;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Platform.IntegrationTests;

/// <summary>Notifications design §2.1: a one-off notice, published on a connection of its own.</summary>
public class NotifierTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Hook = "https://discord.com/api/webhooks/777/householdTokenabcd";
    private const string PersonalHook = "https://discord.com/api/webhooks/778/personalTokenabcd";

    private static readonly Dictionary<string, string> Smtp = new()
    {
        ["Nid:Smtp:Host"] = "smtp.test.localhost",
        ["Nid:Smtp:From"] = "Nid <nid@test.localhost>",
    };

    private sealed class Quiet : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
    }

    private sealed class LogCapture : ILoggerProvider
    {
        public List<LogLevel> Levels { get; } = [];


        public ILogger CreateLogger(string categoryName) => categoryName == typeof(Nid.Platform.Infrastructure.Notifications.Notifier).FullName ? new Capture(this) : NullLogger.Instance;

        public void Dispose()
        {
        }

        private sealed class Capture(LogCapture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Levels)
                    owner.Levels.Add(logLevel);
            }
        }
    }

    private async Task<NidApp> StartAsync(Action<IServiceCollection>? more = null, bool email = false)
    {
        var app = await NidApp.StartAsync(
            fixture,
            services =>
            {
                services.RemoveAll<SafeHttpClient>();
                services.AddSingleton(new SafeHttpClient(new HttpMessageInvoker(new Quiet()), NidOutbound.Options(), NullLogger<SafeHttpClient>.Instance));
                more?.Invoke(services);
            },
            email ? Smtp : null);
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        core.Persons.Add(new PersonRow { Id = Guid.CreateVersion7(), Kind = "adult", OidcSubject = null, DisplayName = "pending", Email = "pending@example.test", CreatedAt = NodaTime.SystemClock.Instance.GetCurrentInstant() });
        if (email)
            (await core.Persons.SingleAsync(p => p.Id == app.AdultA.Value)).Email = "a@example.test";
        await core.SaveChangesAsync();
        if (email)
        {
            var saved = await app.RawAsync(
                app.AdultA, HttpMethod.Put, "/api/v1/platform/notifications/preferences",
                new { emailEnabled = true, action = "immediate", info = "digest", frequency = "weekly", weekday = 1, leadDays = new Dictionary<string, int>(), version = 0 });
            Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        }

        return app;
    }

    private sealed class SettableClock(Instant now) : IClock
    {
        public Instant Now { get; set; } = now;

        public Instant GetCurrentInstant() => Now;
    }

    private static INotifier NotifierOf(NidApp app) => app.Services.GetRequiredService<INotifier>();

    private static Notice NoticeOf(string kind, Recipients to, string key, params string[] parameters)
        => new(kind, to, key, [], parameters.Chunk(2).ToDictionary(p => p[0], p => p[1]));

    private static async Task<List<NotificationRow>> RowsAsync(NidApp app, string key)
    {
        await using var db = PostgresFixture.NewPlatformContext(app.ConnectionString);
        return await db.Notifications.AsNoTracking().Where(n => n.Key == key).ToListAsync();
    }

    private static async Task<List<NotificationDeliveryRow>> DeliveriesAsync(NidApp app, string channel)
    {
        await using var db = PostgresFixture.NewPlatformContext(app.ConnectionString);
        return await db.Deliveries.AsNoTracking().Where(d => d.Channel == channel).ToListAsync();
    }

    [Fact]
    public async Task Publish_to_all_adults_writes_one_open_row_each()
    {
        await using var app = await StartAsync();

        await NotifierOf(app).PublishAsync(NoticeOf("access_violation", new Recipients.AllAdults(), "access_violation:1", "request", "GetX"), TestContext.Current.CancellationToken);

        var rows = await RowsAsync(app, "access_violation:1");
        Assert.Equal(new[] { app.AdultA.Value, app.AdultB.Value }.Order(), rows.Select(r => r.PersonId).Order());
        Assert.All(rows, r =>
        {
            Assert.Equal((NotificationStates.Open, 1, "critical"), (r.State, r.Round, r.Level));
            Assert.Null(r.ReadAt);
        });
    }

    [Fact]
    public async Task Publish_to_admins_and_to_people()
    {
        await using var app = await StartAsync();
        var notifier = NotifierOf(app);

        await notifier.PublishAsync(NoticeOf("job_failed", new Recipients.Admins(), "job_failed:a"), TestContext.Current.CancellationToken);
        await notifier.PublishAsync(NoticeOf("proposal_pending", new Recipients.People([app.AdultB, app.Dependent]), "proposal_pending:1"), TestContext.Current.CancellationToken);

        Assert.Equal(app.AdultA.Value, Assert.Single(await RowsAsync(app, "job_failed:a")).PersonId);
        Assert.Equal(app.AdultB.Value, Assert.Single(await RowsAsync(app, "proposal_pending:1")).PersonId);
    }

    [Fact]
    public async Task Publishing_twice_is_one_row()
    {
        await using var app = await StartAsync(email: true);
        var notice = NoticeOf("job_failed", new Recipients.Admins(), "job_failed:a");

        await NotifierOf(app).PublishAsync(notice, TestContext.Current.CancellationToken);
        await NotifierOf(app).PublishAsync(notice, TestContext.Current.CancellationToken);

        var row = Assert.Single(await RowsAsync(app, "job_failed:a"));
        Assert.Equal(1, row.Round);
        Assert.Single(await DeliveriesAsync(app, "email"));
    }

    [Fact]
    public async Task Resolve_then_publish_reopens_with_a_new_round()
    {
        await using var app = await StartAsync(email: true);
        var notice = NoticeOf("job_failed", new Recipients.Admins(), "job_failed:a");
        var notifier = NotifierOf(app);

        await notifier.PublishAsync(notice, TestContext.Current.CancellationToken);
        await notifier.ResolveAsync("job_failed:a", TestContext.Current.CancellationToken);
        Assert.Equal(NotificationStates.Resolved, Assert.Single(await RowsAsync(app, "job_failed:a")).State);
        await notifier.PublishAsync(notice, TestContext.Current.CancellationToken);

        var row = Assert.Single(await RowsAsync(app, "job_failed:a"));
        Assert.Equal((NotificationStates.Open, 2), (row.State, row.Round));
        Assert.Equal([1, 2], (await DeliveriesAsync(app, "email")).Select(d => d.Round).Order());
    }

    [Fact]
    public async Task Resolve_settles_a_dismissed_row()
    {
        await using var app = await StartAsync();
        var notifier = NotifierOf(app);
        await notifier.PublishAsync(NoticeOf("access_violation", new Recipients.AllAdults(), "access_violation:2"), TestContext.Current.CancellationToken);
        var bob = (await RowsAsync(app, "access_violation:2")).Single(r => r.PersonId == app.AdultB.Value);
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<INotificationStore>().DismissAsync(bob.Id, NodaTime.SystemClock.Instance.GetCurrentInstant(), TestContext.Current.CancellationToken);

        await notifier.PublishAsync(NoticeOf("access_violation", new Recipients.AllAdults(), "access_violation:2"), TestContext.Current.CancellationToken);
        Assert.Equal(NotificationStates.Dismissed, (await RowsAsync(app, "access_violation:2")).Single(r => r.Id == bob.Id).State);

        await notifier.ResolveAsync("access_violation:2", TestContext.Current.CancellationToken);
        Assert.All(await RowsAsync(app, "access_violation:2"), r => Assert.Equal(NotificationStates.Resolved, r.State));
        await notifier.PublishAsync(NoticeOf("access_violation", new Recipients.AllAdults(), "access_violation:2"), TestContext.Current.CancellationToken);
        Assert.All(await RowsAsync(app, "access_violation:2"), r => Assert.Equal((NotificationStates.Open, 2), (r.State, r.Round)));
    }

    [Fact]
    public async Task Publish_survives_a_rolled_back_request()
    {
        await using var app = await StartAsync();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await using var unit = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(TestContext.Current.CancellationToken);
            await NotifierOf(app).PublishAsync(NoticeOf("access_violation", new Recipients.AllAdults(), "access_violation:3"), TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, (await RowsAsync(app, "access_violation:3")).Count);
    }

    [Fact]
    public async Task Publish_never_fails_the_caller()
    {
        var logs = new LogCapture();
        await using var app = await StartAsync(services =>
        {
            services.AddSingleton<ILoggerProvider>(logs);
            services.AddScoped<INotificationStore>(_ => throw new InvalidOperationException("The store is down."));
        });

        await NotifierOf(app).PublishAsync(NoticeOf("access_violation", new Recipients.AllAdults(), "access_violation:4"), TestContext.Current.CancellationToken);
        await NotifierOf(app).ResolveAsync("access_violation:4", TestContext.Current.CancellationToken);
        await NotifierOf(app).PublishAsync(NoticeOf("unknown_kind", new Recipients.AllAdults(), "x:1"), TestContext.Current.CancellationToken);

        Assert.Equal([LogLevel.Error, LogLevel.Error, LogLevel.Error], logs.Levels);
    }

    [Fact]
    public async Task Household_gets_item_less_security_and_health_rows()
    {
        var clock = new SettableClock(Instant.FromUtc(2025, 10, 15, 8, 0));
        await using var app = await StartAsync(services => services.AddSingleton<IClock>(clock));
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
        {
            (await core.Persons.SingleAsync(p => p.Id == app.AdultB.Value, cancellationToken: TestContext.Current.CancellationToken)).IsAdmin = true; // two admins: still one post per publication
            await core.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.True((await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/admin/notifications/discord", new { url = Hook })).IsSuccessStatusCode);
        var notifier = NotifierOf(app);

        await notifier.PublishAsync(NoticeOf("access_violation", new Recipients.AllAdults(), "access_violation:5"), TestContext.Current.CancellationToken);
        await notifier.PublishAsync(NoticeOf("job_failed", new Recipients.Admins(), "job_failed:x"), TestContext.Current.CancellationToken);
        await notifier.ResolveAsync("job_failed:x", TestContext.Current.CancellationToken);
        clock.Now = Instant.FromUtc(2025, 10, 16, 8, 0);
        await notifier.PublishAsync(NoticeOf("job_failed", new Recipients.Admins(), "job_failed:x"), TestContext.Current.CancellationToken);

        Assert.Equal(
            ["access_violation:5", $"job_failed:x@{Instant.FromUtc(2025, 10, 15, 8, 0).ToUnixTimeTicks()}", $"job_failed:x@{clock.Now.ToUnixTimeTicks()}"],
            (await DeliveriesAsync(app, "discord_household")).Select(d => d.HouseholdKey!).Order());

        // After the 90-day purge the household keys stay spent, yet a new crash of the same job posts again.
        await notifier.ResolveAsync("job_failed:x", TestContext.Current.CancellationToken);
        await using (var scope = app.Services.CreateAsyncScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<INotificationStore>().PurgeAsync(clock.Now + NodaTime.Duration.FromDays(200), scope.ServiceProvider.GetRequiredService<NotificationCatalog>().Kinds.Keys.ToList(), TestContext.Current.CancellationToken) > 0);
        Assert.Empty(await RowsAsync(app, "job_failed:x"));
        clock.Now = Instant.FromUtc(2026, 5, 1, 8, 0);
        await notifier.PublishAsync(NoticeOf("job_failed", new Recipients.Admins(), "job_failed:x"), TestContext.Current.CancellationToken);

        Assert.Equal(2, (await RowsAsync(app, "job_failed:x")).Count);
        Assert.Equal(4, (await DeliveriesAsync(app, "discord_household")).Count);
    }

    private static async Task<List<NotificationDeliveryRow>> DeliveriesOfAsync(NidApp app, Guid notification)
    {
        await using var db = PostgresFixture.NewPlatformContext(app.ConnectionString);
        return await db.Deliveries.AsNoTracking().Where(d => d.NotificationId == notification).ToListAsync();
    }

    [Fact]
    public async Task An_access_violation_is_one_notice_per_request_and_day_and_ends_when_read()
    {
        var clock = new SettableClock(Instant.FromUtc(2025, 10, 15, 8, 0));
        await using var app = await StartAsync(services => services.AddSingleton<IClock>(clock), email: true);
        Assert.True((await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/admin/notifications/discord", new { url = Hook })).IsSuccessStatusCode);
        Assert.True((await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/platform/notifications/discord", new { url = PersonalHook })).IsSuccessStatusCode);

        async Task ViolateAsync()
        {
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IAccessViolations>().RecordAsync("GetX", app.AdultA, [], new AccessViolationCause(AccessViolationReasons.OutsideVisibleSet), default);
        }

        for (var i = 0; i < 3; i++)
            await ViolateAsync();

        var first = await RowsAsync(app, "access_violation:GetX:2025-10-15");
        Assert.Equal(new[] { app.AdultA.Value, app.AdultB.Value }.Order(), first.Select(r => r.PersonId).Order());
        Assert.All(first, r => Assert.Equal((NotificationStates.Open, 1), (r.State, r.Round)));
        foreach (var channel in new[] { "email", "discord_person", "discord_household" })
            Assert.Single(await DeliveriesAsync(app, channel));

        clock.Now = Instant.FromUtc(2025, 10, 16, 8, 0);
        await ViolateAsync();
        var second = await RowsAsync(app, "access_violation:GetX:2025-10-16");
        Assert.Equal(2, second.Count);
        Assert.Equal(2, (await DeliveriesAsync(app, "discord_household")).Count);

        // A critical published row ends when it is read, by one (/read) or all (/read-all): nothing else would ever resolve it.
        var alice = first.Single(r => r.PersonId == app.AdultA.Value);
        Assert.Null((await app.InvokeAsync(app.AdultA, new ReadNotificationCommand(alice.Id), TestContext.Current.CancellationToken)).Failure);
        Assert.Null((await app.InvokeAsync(app.AdultB, new ReadAllNotificationsCommand(), TestContext.Current.CancellationToken)).Failure);

        await using (var db = PostgresFixture.NewPlatformContext(app.ConnectionString))
        {
            var rows = await db.Notifications.AsNoTracking().Where(n => n.Kind == "access_violation").ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
            var open = Assert.Single(rows, r => r.State == NotificationStates.Open);
            Assert.Equal((app.AdultA.Value, "access_violation:GetX:2025-10-16"), (open.PersonId, open.Key));
            Assert.All(rows.Where(r => r.Id != open.Id), r => Assert.Equal((NotificationStates.Resolved, (Instant?)clock.Now, (Instant?)clock.Now), (r.State, r.ResolvedAt, r.ReadAt)));
        }

        await using (var scope = app.Services.CreateAsyncScope())
            Assert.Equal(3, await scope.ServiceProvider.GetRequiredService<INotificationStore>().PurgeAsync(clock.Now + NodaTime.Duration.FromDays(91), scope.ServiceProvider.GetRequiredService<NotificationCatalog>().Kinds.Keys.ToList(), TestContext.Current.CancellationToken));
        Assert.Empty(await RowsAsync(app, "access_violation:GetX:2025-10-15"));
    }

    [Fact]
    public async Task A_pending_proposal_never_reaches_the_household()
    {
        await using var app = await StartAsync();
        Assert.True((await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/admin/notifications/discord", new { url = Hook })).IsSuccessStatusCode);
        var shared = await app.NewSubscriptionAsync("joint", app.AdultA, Visibility.Shared);

        await NotifierOf(app).PublishAsync(new Notice("proposal_pending", new Recipients.AllAdults(), "proposal_pending:9", [shared], new Dictionary<string, string>()), TestContext.Current.CancellationToken);
        await NotifierOf(app).PublishAsync(new Notice("proposal_pending", new Recipients.AllAdults(), "proposal_pending:10", [], new Dictionary<string, string>()), TestContext.Current.CancellationToken);
        await NotifierOf(app).PublishAsync(NoticeOf("access_violation", new Recipients.AllAdults(), "access_violation:9"), TestContext.Current.CancellationToken);

        Assert.Equal(4, (await RowsAsync(app, "proposal_pending:9")).Count + (await RowsAsync(app, "proposal_pending:10")).Count);
        Assert.Equal(["access_violation:9"], (await DeliveriesAsync(app, "discord_household")).Select(d => d.HouseholdKey!));
    }

    [Fact]
    public async Task A_snoozed_published_row_wakes_with_the_evaluation()
    {
        var clock = new SettableClock(Instant.FromUtc(2025, 10, 15, 8, 0));
        await using var app = await StartAsync(services => services.AddSingleton<IClock>(clock), email: true);
        await NotifierOf(app).PublishAsync(NoticeOf("proposal_pending", new Recipients.People([app.AdultA]), "proposal_pending:11"), TestContext.Current.CancellationToken);
        var row = Assert.Single(await RowsAsync(app, "proposal_pending:11"));
        Assert.Single(await DeliveriesOfAsync(app, row.Id));

        Assert.Null((await app.InvokeAsync(app.AdultA, new SnoozeNotificationCommand(row.Id, new LocalDate(2025, 10, 16), null), TestContext.Current.CancellationToken)).Failure);
        Assert.Null((await app.InvokeAsync(app.AdultA, new EvaluateNotificationsCommand(app.AdultA, true), TestContext.Current.CancellationToken)).Failure);
        Assert.Equal(NotificationStates.Snoozed, Assert.Single(await RowsAsync(app, "proposal_pending:11")).State);

        clock.Now = Instant.FromUtc(2025, 10, 16, 8, 0);
        Assert.Null((await app.InvokeAsync(app.AdultA, new EvaluateNotificationsCommand(app.AdultA, true), TestContext.Current.CancellationToken)).Failure);

        var woken = Assert.Single(await RowsAsync(app, "proposal_pending:11"));
        Assert.Equal((NotificationStates.Open, 2, (LocalDate?)null, (Instant?)null), (woken.State, woken.Round, woken.SnoozedUntil, woken.ReadAt));
        Assert.Equal(clock.Now, woken.OpenedAt);
        Assert.Equal([1, 2], (await DeliveriesOfAsync(app, row.Id)).Select(d => d.Round).Order());
    }
}
