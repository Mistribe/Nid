using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Platform.Infrastructure.Persistence;
using Nid.Platform.Notifications;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Subscriptions.IntegrationTests.Notifications;

/// <summary>Notifications design §3.3: the evaluation of the sources, once a day after 06:00, and its idempotence.</summary>
public class EvaluationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly NotificationKind Dated = new("fake_dated", "fake", NotificationLevel.Action, "fake", 90);
    private static readonly NotificationKind Undated = new("fake_undated", "fake", NotificationLevel.Info, "fake", null);

    // Household zone: Europe/Paris, UTC+2 until 2025-10-26.
    private static readonly Instant Before0600 = Instant.FromUtc(2025, 10, 15, 3, 30);
    private static readonly Instant After0600 = Instant.FromUtc(2025, 10, 15, 4, 10);
    private static readonly Instant Later = Instant.FromUtc(2025, 10, 15, 5, 0);

    private sealed class SettableClock(Instant now) : IClock
    {
        public Instant Now { get; set; } = now;

        public Instant GetCurrentInstant() => Now;
    }

    private sealed class FakeNotificationSource : INotificationSource
    {
        public string Name => "fake";

        public IReadOnlyList<NotificationKind> Kinds { get; } = [Dated, Undated];

        public List<Signal> Signals { get; } = [];

        public bool Fails { get; set; }

        public List<PersonId> Readers { get; } = [];

        public Task<Result<IReadOnlyList<Signal>>> SignalsAsync(PersonId reader, LocalDate today, LocalDate horizon, CancellationToken ct)
        {
            lock (Readers)
                Readers.Add(reader);
            return Task.FromResult(Fails
                ? Result.Failure<IReadOnlyList<Signal>>(new Failure("fake.down", "The source is down.", null))
                : Result.Success<IReadOnlyList<Signal>>(Signals.ToList()));
        }
    }

    private sealed class LogCapture : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capture(this);

        public void Dispose()
        {
        }

        private sealed class Capture(LogCapture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Entries)
                    owner.Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private sealed record Harness(NidApp App, FakeNotificationSource Source, SettableClock Clock, LogCapture Logs) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private async Task<Harness> StartAsync(Instant now)
    {
        var source = new FakeNotificationSource();
        var clock = new SettableClock(now);
        var logs = new LogCapture();
        var app = await NidApp.StartAsync(fixture, services =>
        {
            services.AddSingleton<IClock>(clock);
            services.RemoveAll<INotificationSource>(); // only the fake one speaks here
            services.AddSingleton<INotificationSource>(source);
            services.AddLogging(b => b.AddProvider(logs));
        });
        return new Harness(app, source, clock, logs);
    }

    private static Signal SignalOf(string kind, string key, LocalDate? due = null)
        => new(key, kind, [new ItemId(Guid.NewGuid())], due, new Dictionary<string, string>());

    private static async Task RunAsync(Harness h, bool force, PersonId? only = null)
    {
        var (_, failure) = await h.App.InvokeAsync(h.App.AdultA, new EvaluateNotificationsCommand(only, force));
        Assert.Null(failure);
    }

    private static async Task<List<NotificationRow>> RowsAsync(NidApp app, PersonId person)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        return await db.Notifications.AsNoTracking().Where(n => n.PersonId == person.Value).OrderBy(n => n.Key).ToListAsync();
    }

    private static async Task<List<NotificationRunRow>> RunsAsync(NidApp app, PersonId person)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        return await db.NotificationRuns.AsNoTracking().Where(n => n.PersonId == person.Value).ToListAsync();
    }

    private sealed class ActorProbe(IActorAccessor accessor, List<(PersonId Reader, Actor? Seen)> seen) : INotificationSource
    {
        public string Name => "probe";

        public IReadOnlyList<NotificationKind> Kinds { get; } = [Undated];

        public Task<Result<IReadOnlyList<Signal>>> SignalsAsync(PersonId reader, LocalDate today, LocalDate horizon, CancellationToken ct)
        {
            lock (seen)
                seen.Add((reader, accessor.Current));
            return Task.FromResult(Result.Success<IReadOnlyList<Signal>>([]));
        }
    }

    private sealed class ActorWatchingClock(IClock inner, IActorAccessor accessor, List<Actor?> calls) : IClock
    {
        public Instant GetCurrentInstant()
        {
            lock (calls)
                calls.Add(accessor.Current);
            return inner.GetCurrentInstant();
        }
    }

    [Fact]
    public async Task Sources_read_as_a_read_only_job_actor_and_the_accessor_is_restored()
    {
        var seen = new List<(PersonId Reader, Actor? Seen)>();
        var calls = new List<Actor?>();
        await using var app = await NidApp.StartAsync(fixture, services =>
        {
            services.AddSingleton<IClock>(new SettableClock(After0600));
            services.AddScoped<IClock>(sp => new ActorWatchingClock(new SettableClock(After0600), sp.GetRequiredService<IActorAccessor>(), calls));
            services.RemoveAll<INotificationSource>();
            services.AddScoped<INotificationSource>(sp => new ActorProbe(sp.GetRequiredService<IActorAccessor>(), seen));
        });

        var (_, failure) = await app.InvokeAsync(app.AdultA, new EvaluateNotificationsCommand(null, true));

        Assert.Null(failure);
        Assert.NotEmpty(seen);
        foreach (var (reader, actor) in seen)
        {
            Assert.NotNull(actor);
            Assert.Equal(ActorKind.Person, actor!.Kind);
            Assert.Equal(reader, actor.PersonId);
            Assert.Equal(ActorOrigin.Job, actor.Via);
            Assert.False(actor.Has(ScopeNames.Write));
        }
        var last = calls.Last();
        Assert.NotNull(last);
        Assert.Equal(app.AdultA, last!.PersonId);
        Assert.NotEqual(ActorOrigin.Job, last.Via);
    }

    [Fact]
    public async Task Second_run_the_same_day_changes_nothing()
    {
        await using var h = await StartAsync(After0600);
        h.Source.Signals.Add(SignalOf(Undated.Kind, "u:1"));

        await RunAsync(h, force: true);
        var first = await RowsAsync(h.App, h.App.AdultA);
        h.Clock.Now = Later;
        await RunAsync(h, force: true);
        var second = await RowsAsync(h.App, h.App.AdultA);

        var row = Assert.Single(first);
        Assert.Equal(NotificationStates.Open, row.State);
        Assert.Equal(row.Id, Assert.Single(second).Id);
        Assert.Equal(row.OpenedAt, second[0].OpenedAt);
    }

    [Fact]
    public async Task Once_a_day_after_six()
    {
        await using var h = await StartAsync(Before0600);
        h.Source.Signals.Add(SignalOf(Undated.Kind, "u:1"));

        await RunAsync(h, force: false);
        Assert.Empty(await RowsAsync(h.App, h.App.AdultA));

        h.Clock.Now = After0600;
        await RunAsync(h, force: false);
        Assert.Single(await RowsAsync(h.App, h.App.AdultA));
        var ranAt = Assert.Single(await RunsAsync(h.App, h.App.AdultA)).RanAt;
        Assert.Equal(After0600, ranAt);

        h.Clock.Now = Later;
        await RunAsync(h, force: false);
        Assert.Equal(ranAt, Assert.Single(await RunsAsync(h.App, h.App.AdultA)).RanAt);
    }

    [Fact]
    public async Task Dependents_are_never_evaluated()
    {
        await using var h = await StartAsync(After0600);

        await RunAsync(h, force: true);

        Assert.Equal(new[] { h.App.AdultA, h.App.AdultB }.OrderBy(p => p.Value), h.Source.Readers.Distinct().OrderBy(p => p.Value));
        Assert.DoesNotContain(h.App.Dependent, h.Source.Readers);
    }

    [Fact]
    public async Task A_failing_source_resolves_nothing()
    {
        await using var h = await StartAsync(After0600);
        h.Source.Signals.Add(SignalOf(Undated.Kind, "u:1"));
        await RunAsync(h, force: true);
        Assert.Equal(NotificationStates.Open, Assert.Single(await RowsAsync(h.App, h.App.AdultA)).State);

        h.Clock.Now = After0600 + NodaTime.Duration.FromDays(1);
        h.Source.Fails = true;
        await RunAsync(h, force: true);
        Assert.Equal(NotificationStates.Open, Assert.Single(await RowsAsync(h.App, h.App.AdultA)).State);
        Assert.False(Assert.Single(await RunsAsync(h.App, h.App.AdultA)).Ok);
        Assert.Contains(h.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("fake"));

        h.Clock.Now = After0600 + NodaTime.Duration.FromDays(2);
        h.Source.Fails = false;
        h.Source.Signals.Clear();
        await RunAsync(h, force: true);
        Assert.Equal(NotificationStates.Resolved, Assert.Single(await RowsAsync(h.App, h.App.AdultA)).State);
        Assert.True(Assert.Single(await RunsAsync(h.App, h.App.AdultA)).Ok);
    }

    [Fact]
    public async Task Person_lead_days_retime_pending_rows()
    {
        await using var h = await StartAsync(After0600);
        var item = new ItemId(Guid.NewGuid());
        var due = new LocalDate(2026, 3, 31);
        h.Source.Signals.Add(new Signal(SignalKeys.Dated(Dated.Kind, item, due), Dated.Kind, [item], due, new Dictionary<string, string>()));
        await RunAsync(h, force: true);
        var before = Assert.Single(await RowsAsync(h.App, h.App.AdultA));
        Assert.Equal((NotificationStates.Pending, new LocalDate(2025, 12, 31)), (before.State, before.NotifyOn));

        var saved = await h.App.RawAsync(h.App.AdultA, HttpMethod.Put, "/api/v1/platform/notifications/preferences", new
        {
            emailEnabled = false, action = "immediate", info = "digest", frequency = "weekly", weekday = 1,
            leadDays = new Dictionary<string, int> { [Dated.Kind] = 30 }, version = 0,
        });
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        h.Clock.Now = Later;
        await RunAsync(h, force: true);

        var after = Assert.Single(await RowsAsync(h.App, h.App.AdultA));
        Assert.Equal(before.Id, after.Id);
        Assert.Equal((NotificationStates.Pending, new LocalDate(2026, 3, 1)), (after.State, after.NotifyOn));
    }

    [Fact]
    public async Task Moved_due_date_is_a_new_occurrence()
    {
        await using var h = await StartAsync(After0600);
        var item = new ItemId(Guid.NewGuid());
        var oldDue = new LocalDate(2026, 3, 31);
        var newDue = new LocalDate(2026, 6, 30);
        h.Source.Signals.Add(new Signal(SignalKeys.Dated(Dated.Kind, item, oldDue), Dated.Kind, [item], oldDue, new Dictionary<string, string>()));
        await RunAsync(h, force: true);

        h.Source.Signals.Clear();
        h.Source.Signals.Add(new Signal(SignalKeys.Dated(Dated.Kind, item, newDue), Dated.Kind, [item], newDue, new Dictionary<string, string>()));
        await RunAsync(h, force: true);

        var rows = await RowsAsync(h.App, h.App.AdultA);
        Assert.Equal(2, rows.Count);
        var old = rows.Single(r => r.Key.EndsWith("2026-03-31"));
        var moved = rows.Single(r => r.Key.EndsWith("2026-06-30"));
        Assert.Equal(NotificationStates.Resolved, old.State);
        Assert.Equal((NotificationStates.Pending, new LocalDate(2026, 4, 1)), (moved.State, moved.NotifyOn));
    }

    [Fact]
    public async Task A_store_failure_for_one_person_does_not_stop_the_others()
    {
        var source = new FakeNotificationSource();
        var logs = new LogCapture();
        PersonId? failFor = null;
        await using var app = await NidApp.StartAsync(fixture, services =>
        {
            services.AddSingleton<IClock>(new SettableClock(After0600));
            services.RemoveAll<INotificationSource>(); // only the fake one speaks here
            services.AddSingleton<INotificationSource>(source);
            services.AddLogging(b => b.AddProvider(logs));
            services.AddScoped<INotificationStore>(sp => new ThrowingNotificationStore(
                ActivatorUtilities.CreateInstance<Nid.Platform.Infrastructure.Notifications.NotificationStore>(sp), failFor!.Value));
        });
        failFor = app.AdultA;
        source.Signals.Add(SignalOf(Undated.Kind, "u:1"));

        var h = new Harness(app, source, new SettableClock(After0600), logs);
        await RunAsync(h, force: true);

        Assert.Empty(await RowsAsync(app, app.AdultA));
        Assert.Single(await RowsAsync(app, app.AdultB));
        Assert.False(Assert.Single(await RunsAsync(app, app.AdultA)).Ok);
        Assert.True(Assert.Single(await RunsAsync(app, app.AdultB)).Ok);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("store failed"));
    }

    [Fact]
    public async Task Unknown_kind_is_dropped()
    {
        await using var h = await StartAsync(After0600);
        h.Source.Signals.Add(SignalOf("nope", "nope:1"));

        await RunAsync(h, force: true);

        Assert.Empty(await RowsAsync(h.App, h.App.AdultA));
        Assert.True(Assert.Single(await RunsAsync(h.App, h.App.AdultA)).Ok);
        Assert.Contains(h.Logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("nope"));
    }

    [Fact]
    public async Task A_resolved_key_signalled_again_reopens_unread()
    {
        await using var h = await StartAsync(After0600);
        h.Source.Signals.Add(SignalOf(Undated.Kind, "u:1"));
        await RunAsync(h, force: true);
        await using (var scope = h.App.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<INotificationStore>();
            await store.MarkReadAsync(h.App.AdultA, [Assert.Single(await RowsAsync(h.App, h.App.AdultA)).Id], After0600, default);
        }

        h.Clock.Now = After0600 + NodaTime.Duration.FromDays(1);
        var signal = h.Source.Signals.Single();
        h.Source.Signals.Clear();
        await RunAsync(h, force: true);
        var resolved = Assert.Single(await RowsAsync(h.App, h.App.AdultA));
        Assert.Equal(NotificationStates.Resolved, resolved.State);

        h.Clock.Now = After0600 + NodaTime.Duration.FromDays(2);
        h.Source.Signals.Add(signal);
        await RunAsync(h, force: true);
        var again = Assert.Single(await RowsAsync(h.App, h.App.AdultA));
        Assert.Equal(resolved.Id, again.Id);
        Assert.Equal(NotificationStates.Open, again.State);
        Assert.Null(again.ReadAt);
        Assert.Null(again.ResolvedAt);
        Assert.Equal(h.Clock.Now, again.OpenedAt);
    }

    [Fact]
    public async Task A_dismissed_row_is_never_purged_while_signalled_and_is_settled_once_it_stops()
    {
        await using var h = await StartAsync(After0600);
        var id = Guid.NewGuid();
        await using (var scope = h.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.Notifications.Add(new NotificationRow
            {
                Id = id, PersonId = h.App.AdultA.Value, Kind = Undated.Kind, Key = "d:1", Level = "info",
                NotifyOn = new LocalDate(2025, 6, 1), State = NotificationStates.Dismissed,
                CreatedAt = After0600 - NodaTime.Duration.FromDays(120), DismissedAt = After0600 - NodaTime.Duration.FromDays(100),
            });
            await db.SaveChangesAsync();
        }

        h.Source.Signals.Add(SignalOf(Undated.Kind, "d:1"));
        await RunAsync(h, force: true);
        var signalled = Assert.Single(await RowsAsync(h.App, h.App.AdultA));
        Assert.Equal(NotificationStates.Dismissed, signalled.State);
        Assert.Null(signalled.ResolvedAt);

        h.Clock.Now = After0600 + NodaTime.Duration.FromDays(1);
        h.Source.Signals.Clear();
        await RunAsync(h, force: true);
        var settled = Assert.Single(await RowsAsync(h.App, h.App.AdultA));
        Assert.Equal(NotificationStates.Dismissed, settled.State);
        Assert.Equal(h.Clock.Now, settled.ResolvedAt);

        h.Clock.Now = After0600 + NodaTime.Duration.FromDays(92);
        await RunAsync(h, force: true);
        Assert.DoesNotContain(await RowsAsync(h.App, h.App.AdultA), r => r.Id == id);
    }

    [Fact]
    public async Task Purge_runs_with_evaluation()
    {
        await using var h = await StartAsync(After0600);
        var oldId = Guid.NewGuid();
        await using (var scope = h.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.Notifications.Add(new NotificationRow
            {
                Id = oldId, PersonId = h.App.AdultA.Value, Kind = Undated.Kind, Key = "old:1", Level = "info",
                NotifyOn = new LocalDate(2025, 6, 1), State = NotificationStates.Resolved,
                CreatedAt = After0600 - NodaTime.Duration.FromDays(120), ResolvedAt = After0600 - NodaTime.Duration.FromDays(100),
            });
            await db.SaveChangesAsync();
        }

        await RunAsync(h, force: true);

        Assert.DoesNotContain(await RowsAsync(h.App, h.App.AdultA), r => r.Id == oldId);
    }
}
