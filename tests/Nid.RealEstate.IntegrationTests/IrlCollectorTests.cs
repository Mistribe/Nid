using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Application.Admin;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Application.Irl;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>Rental spec §3.1: the weekly collection of the INSEE series, against the recorded document of <c>tests/vectors/irl/</c>.</summary>
public class IrlCollectorTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 23, 7, 0);
    private static readonly LocalDate Today = new(2026, 10, 23);

    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    private sealed class RecordingTrigger : IIrlJobTrigger
    {
        public int Calls;
        public Task<Result> TriggerAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Result.Success());
        }
    }

    private static byte[] Vector() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "vectors", "irl", "insee-001515333.xml"));

    private static Task<NidApp> StartAsync(PostgresFixture fixture, Action<IServiceCollection>? more = null)
        => NidApp.StartAsync(fixture, services =>
        {
            services.AddSingleton<IClock>(new FixedClock(Now));
            more?.Invoke(services);
        });

    private static async Task EnableAsync(NidApp app)
    {
        var (_, failure) = await app.InvokeAsync(app.AdultA, new UpdateHouseholdSettingsCommand(null, null, null, true));
        Assert.Null(failure);
        app.Irl.FailWith = null;
        app.Irl.Body = Vector();
    }

    private static AsyncServiceScope Scope(NidApp app)
    {
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }

    private static async Task<IReadOnlyList<IrlValue>> StoredAsync(NidApp app)
    {
        await using var scope = Scope(app);
        return await scope.ServiceProvider.GetRequiredService<IIrlStore>().ListAsync(default);
    }

    private static async Task<IrlRunView> CollectAsync(NidApp app)
    {
        var (run, failure) = await app.InvokeAsync(app.AdultA, new CollectIrlCommand());
        Assert.Null(failure);
        return (IrlRunView)run!;
    }

    private static (string, int, int, int) Counts(IrlRunView run) => (run.Status, run.Added, run.Replaced, run.Rejected);

    private static async Task Put(NidApp app, IrlValue value)
    {
        await using var scope = Scope(app);
        await scope.ServiceProvider.GetRequiredService<IIrlStore>().UpsertAsync(value, default);
    }

    [Fact]
    public async Task The_recorded_series_is_stored_then_a_second_run_adds_nothing()
    {
        await using var app = await StartAsync(fixture);
        await EnableAsync(app);

        var first = await CollectAsync(app);
        Assert.Equal(("succeeded", 12, 0, 0), Counts(first));
        Assert.Null(first.Message);
        var stored = await StoredAsync(app);
        Assert.Equal(12, stored.Count);
        var q2 = stored.Single(v => v.Quarter == new IrlQuarter(2026, 2));
        Assert.Equal(new IrlValue(new IrlQuarter(2026, 2), 148.37m, new LocalDate(2026, 7, 12), Today, "insee"), q2);

        Assert.Equal(("succeeded", 0, 0, 0), Counts(await CollectAsync(app)));
        Assert.Equal(12, (await StoredAsync(app)).Count);
    }

    [Fact]
    public async Task A_manual_value_is_replaced_by_insees_and_counted()
    {
        await using var app = await StartAsync(fixture);
        await EnableAsync(app);
        await Put(app, new IrlValue(new IrlQuarter(2026, 2), 148.00m, null, new LocalDate(2026, 7, 1), "manual"));

        Assert.Equal(("succeeded", 11, 1, 0), Counts(await CollectAsync(app)));

        var q2 = (await StoredAsync(app)).Single(v => v.Quarter == new IrlQuarter(2026, 2));
        Assert.Equal(("insee", 148.37m), (q2.Source, q2.Value));
    }

    [Fact]
    public async Task A_stored_insee_value_is_never_touched_even_when_insee_revises_it()
    {
        await using var app = await StartAsync(fixture);
        await EnableAsync(app);
        await Put(app, new IrlValue(new IrlQuarter(2026, 2), 148.00m, new LocalDate(2026, 7, 10), new LocalDate(2026, 7, 11), "insee"));

        Assert.Equal(("succeeded", 11, 0, 0), Counts(await CollectAsync(app)));

        Assert.Equal(
            new IrlValue(new IrlQuarter(2026, 2), 148.00m, new LocalDate(2026, 7, 10), new LocalDate(2026, 7, 11), "insee"),
            (await StoredAsync(app)).Single(v => v.Quarter == new IrlQuarter(2026, 2)));
    }

    [Fact]
    public async Task An_implausible_quarter_is_rejected_named_in_the_message_and_the_others_are_stored()
    {
        await using var app = await StartAsync(fixture);
        await EnableAsync(app);
        app.Irl.Body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Vector()).Replace("OBS_VALUE=\"148.37\"", "OBS_VALUE=\"200\""));

        var run = await CollectAsync(app);

        Assert.Equal(("succeeded", 11, 0, 1), Counts(run));
        Assert.Contains("2026-Q2", run.Message);
        Assert.DoesNotContain(await StoredAsync(app), v => v.Quarter == new IrlQuarter(2026, 2));
    }

    [Fact]
    public async Task An_unreachable_source_fails_the_run_and_stores_nothing()
    {
        await using var app = await StartAsync(fixture);
        await EnableAsync(app);
        app.Irl.FailWith = IrlCodes.SourceUnavailable;

        var run = await CollectAsync(app);

        Assert.Equal(("failed", 0, 0, 0), Counts(run));
        Assert.False(string.IsNullOrEmpty(run.Message));
        Assert.Empty(await StoredAsync(app));
    }

    [Fact]
    public async Task A_malformed_answer_fails_the_run_and_stores_nothing()
    {
        await using var app = await StartAsync(fixture);
        await EnableAsync(app);
        app.Irl.Body = "<html>maintenance</html>"u8.ToArray();
        Assert.Equal(("failed", 0, 0, 0), Counts(await CollectAsync(app))); // no observation: a changed format or a wrong series id

        app.Irl.Body = "not xml at all"u8.ToArray();
        var run = await CollectAsync(app);

        Assert.Equal(("failed", 0, 0, 0), Counts(run));
        Assert.NotNull(run.Message);
        Assert.Empty(await StoredAsync(app));
        await using var scope = Scope(app);
        Assert.Equal(["failed", "failed"], (await scope.ServiceProvider.GetRequiredService<IIrlRunLog>().RecentAsync(5, default)).Select(r => r.Status));
    }

    [Fact]
    public async Task Disabled_writes_no_run_and_a_sync_request_is_refused()
    {
        var trigger = new RecordingTrigger();
        await using var app = await StartAsync(fixture, services => services.AddSingleton<IIrlJobTrigger>(trigger));

        var (_, collecting) = await app.InvokeAsync(app.AdultA, new CollectIrlCommand());
        var (_, requesting) = await app.InvokeAsync(app.AdultA, new RequestIrlSyncCommand());

        Assert.Equal((IrlCodes.Disabled, IrlCodes.Disabled), (collecting?.Code, requesting?.Code));
        Assert.Equal(0, trigger.Calls);
        Assert.Equal(0, app.Irl.Requests);
        var (view, _) = await app.InvokeAsync(app.AdultA, new GetIrlQuery());
        Assert.Empty(((IrlAdminView)view!).Runs);
    }

    [Fact]
    public async Task A_sync_request_triggers_the_job_unless_a_run_is_running()
    {
        var trigger = new RecordingTrigger();
        await using var app = await StartAsync(fixture, services => services.AddSingleton<IIrlJobTrigger>(trigger));
        await EnableAsync(app);

        var (requested, failure) = await app.InvokeAsync(app.AdultA, new RequestIrlSyncCommand());
        Assert.Null(failure);
        Assert.Equal(new IrlSyncRequested(true), requested);
        Assert.Equal(1, trigger.Calls);

        await using (var scope = Scope(app))
            await scope.ServiceProvider.GetRequiredService<IIrlRunLog>().TryStartAsync(default);
        var (_, running) = await app.InvokeAsync(app.AdultA, new RequestIrlSyncCommand());
        var (_, collecting) = await app.InvokeAsync(app.AdultA, new CollectIrlCommand());
        Assert.Equal((IrlCodes.SyncRunning, IrlCodes.SyncRunning), (running?.Code, collecting?.Code));
        Assert.Equal(1, trigger.Calls);
    }

    [Fact]
    public async Task Without_jobs_a_sync_request_says_so()
    {
        await using var app = await StartAsync(fixture);
        await EnableAsync(app);

        var (_, failure) = await app.InvokeAsync(app.AdultA, new RequestIrlSyncCommand());

        Assert.Equal(IrlCodes.JobsDisabled, failure?.Code);
    }

    private static readonly LocalDate Jo = new(2026, 10, 15);

    [Fact]
    public async Task A_manual_value_is_stored_replaced_and_refused_once_insee_has_the_quarter()
    {
        await using var app = await StartAsync(fixture);

        var (view, failure) = await app.InvokeAsync(app.AdultA, new SetIrlValueCommand(2026, 3, 148.1m, Jo));
        Assert.Null(failure);
        Assert.Equal(new IrlValueView("2026-Q3", "148.10", Jo, Today, "manual"), view);
        var (_, again) = await app.InvokeAsync(app.AdultA, new SetIrlValueCommand(2026, 3, 148.2m, Jo));
        Assert.Null(again);
        Assert.Equal(148.2m, Assert.Single(await StoredAsync(app)).Value);

        await EnableAsync(app);
        await CollectAsync(app);
        var (_, fromInsee) = await app.InvokeAsync(app.AdultA, new SetIrlValueCommand(2026, 3, 150m, Jo));
        Assert.Equal(IrlCodes.IrlFromInsee, fromInsee?.Code);
        Assert.Equal(148.06m, (await StoredAsync(app)).Single(v => v.Quarter == new IrlQuarter(2026, 3)).Value);
    }

    [Theory]
    [InlineData(2026, 5, 148.0, "2026-10-15", "quarter")]
    [InlineData(2026, 0, 148.0, "2026-10-15", "quarter")]
    [InlineData(1999, 1, 148.0, "2026-10-15", "quarter")]
    [InlineData(2027, 1, 148.0, "2026-10-15", "quarter")]
    [InlineData(2026, 4, 148.0, "2026-10-15", "quarter")] // not over yet
    [InlineData(2026, 1, 0.0, "2026-10-15", "value")]
    [InlineData(2026, 1, -3.0, "2026-10-15", "value")]
    [InlineData(2026, 1, 148.123, "2026-10-15", "value")] // two decimals at most
    [InlineData(2026, 3, 148.0, "2026-10-24", "publishedOn")] // after today
    [InlineData(2026, 3, 148.0, "2026-09-30", "publishedOn")] // before the quarter is over
    public async Task An_invalid_manual_value_is_refused(int year, int quarter, double value, string publishedOn, string field)
    {
        await using var app = await StartAsync(fixture);

        var (_, failure) = await app.InvokeAsync(app.AdultA,
            new SetIrlValueCommand(year, quarter, (decimal)value, NodaTime.Text.LocalDatePattern.Iso.Parse(publishedOn).Value));

        Assert.Equal((IrlCodes.InvalidIrlValue, field), (failure?.Code, failure?.Metadata?["field"]));
        Assert.Empty(await StoredAsync(app));
    }

    [Fact]
    public async Task A_manual_value_far_from_the_same_quarter_a_year_earlier_is_refused()
    {
        await using var app = await StartAsync(fixture);
        await Put(app, new IrlValue(new IrlQuarter(2025, 3), 146.00m, new LocalDate(2025, 10, 15), new LocalDate(2025, 10, 15), "insee"));

        var (_, failure) = await app.InvokeAsync(app.AdultA, new SetIrlValueCommand(2026, 3, 170m, Jo));

        Assert.Equal((IrlCodes.InvalidIrlValue, "value"), (failure?.Code, failure?.Metadata?["field"]));
        Assert.Null((await app.InvokeAsync(app.AdultA, new SetIrlValueCommand(2026, 3, 148.06m, Jo))).Item2);
    }

    [Fact]
    public async Task The_listing_is_newest_first_with_the_recent_runs()
    {
        await using var app = await StartAsync(fixture);
        await EnableAsync(app);
        await CollectAsync(app);

        var (view, failure) = await app.InvokeAsync(app.AdultA, new GetIrlQuery());

        Assert.Null(failure);
        var admin = (IrlAdminView)view!;
        Assert.Equal(["2026-Q3", "2026-Q2"], admin.Values.Take(2).Select(v => v.Quarter));
        Assert.Equal("2023-Q4", admin.Values[^1].Quarter);
        Assert.Equal("succeeded", Assert.Single(admin.Runs).Status);
    }
}
