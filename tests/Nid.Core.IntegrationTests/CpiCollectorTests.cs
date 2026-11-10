using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Application.Admin;
using Nid.Core.Application.Inflation;
using Nid.Core.Contracts;
using Nid.Core.Domain.Inflation;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Core.IntegrationTests;

/// <summary>The weekly collection of the INSEE CPI, the manual values and the shared series, against the recorded document of <c>tests/vectors/cpi/</c>.</summary>
public class CpiCollectorTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 23, 7, 0);
    private static readonly LocalDate Today = new(2026, 10, 23);

    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    private sealed class RecordingTrigger : ICpiJobTrigger
    {
        public int Calls;
        public Task<Result> TriggerAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Result.Success());
        }
    }

    private static string VectorText() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "cpi", "insee-011814630.xml"));
    private static readonly int Definitive = VectorText().Split("OBS_QUAL=\"DEF\"").Length - 1;

    private static Task<NidApp> StartAsync(PostgresFixture fixture, Action<IServiceCollection>? more = null)
        => NidApp.StartAsync(fixture, services =>
        {
            services.AddSingleton<IClock>(new FixedClock(Now));
            more?.Invoke(services);
        });

    private static void Serve(NidApp app, string? body = null)
    {
        app.Cpi.FailWith = null;
        app.Cpi.Body = Encoding.UTF8.GetBytes(body ?? VectorText());
    }

    private static AsyncServiceScope Scope(NidApp app)
    {
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }

    private static async Task<IReadOnlyList<CpiValue>> StoredAsync(NidApp app)
    {
        await using var scope = Scope(app);
        return await scope.ServiceProvider.GetRequiredService<ICpiStore>().ListAsync(default);
    }

    private static async Task Put(NidApp app, params CpiValue[] values)
    {
        await using var scope = Scope(app);
        foreach (var v in values)
            await scope.ServiceProvider.GetRequiredService<ICpiStore>().UpsertAsync(v, default);
    }

    private static async Task<CpiRunView> CollectAsync(NidApp app)
    {
        var (run, failure) = await app.InvokeAsync(app.AdultA, new CollectCpiCommand(), TestContext.Current.CancellationToken);
        Assert.Null(failure);
        return (CpiRunView)run!;
    }

    private static (string, int, int, int) Counts(CpiRunView run) => (run.Status, run.Added, run.Replaced, run.Rejected);

    [Fact]
    public async Task Collect_stores_the_definitive_months()
    {
        await using var app = await StartAsync(fixture);
        Serve(app);

        var run = await CollectAsync(app);

        Assert.Equal(("succeeded", Definitive, 0, 0), Counts(run));
        var stored = await StoredAsync(app);
        Assert.Equal(Definitive, stored.Count);
        Assert.Equal(new CpiValue(new YearMonth(2026, 9), 103.07m, new LocalDate(2026, 10, 16), Today, "insee"), stored.OrderBy(v => v.Month).Last());

        Assert.Equal(("succeeded", 0, 0, 0), Counts(await CollectAsync(app)));
    }

    [Fact]
    public async Task Collect_replaces_a_manual_month()
    {
        await using var app = await StartAsync(fixture);
        Serve(app);
        await Put(app, new CpiValue(new YearMonth(2026, 9), 103.00m, null, new LocalDate(2026, 10, 1), "manual"));

        Assert.Equal(("succeeded", Definitive - 1, 1, 0), Counts(await CollectAsync(app)));

        var m = (await StoredAsync(app)).Single(v => v.Month == new YearMonth(2026, 9));
        Assert.Equal((103.07m, "insee"), (m.Value, m.Source));
    }

    [Fact]
    public async Task Collect_replaces_a_rebased_series_without_rejecting()
    {
        await using var app = await StartAsync(fixture);
        Serve(app);
        await CollectAsync(app);
        var stored = await StoredAsync(app);
        await Put(app, [.. stored.Select(v => v with { Value = Math.Round(v.Value * 0.97m, 4) })]);

        var run = await CollectAsync(app);

        Assert.Equal(("succeeded", 0, Definitive, 0), Counts(run));
        Assert.Equal(103.07m, (await StoredAsync(app)).Single(v => v.Month == new YearMonth(2026, 9)).Value);
    }

    [Fact]
    public async Task Collect_rejects_an_implausible_month_and_names_it()
    {
        await using var app = await StartAsync(fixture);
        Serve(app, VectorText().Replace("OBS_VALUE=\"103.07\"", "OBS_VALUE=\"120\""));

        var run = await CollectAsync(app);

        Assert.True(run.Rejected >= 1);
        Assert.Contains("2026-09", run.Message);
        Assert.DoesNotContain(await StoredAsync(app), v => v.Month == new YearMonth(2026, 9));
    }

    [Fact]
    public async Task Collect_is_refused_when_turned_off()
    {
        var trigger = new RecordingTrigger();
        await using var app = await StartAsync(fixture, services => services.AddSingleton<ICpiJobTrigger>(trigger));
        Serve(app);
        var (_, off) = await app.InvokeAsync(app.AdultA, new UpdateHouseholdSettingsCommand(null, null, null, null, InflationEnabled: false), TestContext.Current.CancellationToken);
        Assert.Null(off);

        var (_, collecting) = await app.InvokeAsync(app.AdultA, new CollectCpiCommand(), TestContext.Current.CancellationToken);
        var (_, requesting) = await app.InvokeAsync(app.AdultA, new RequestCpiSyncCommand(), TestContext.Current.CancellationToken);

        Assert.Equal((InflationCodes.Disabled, InflationCodes.Disabled), (collecting?.Code, requesting?.Code));
        Assert.Equal((0, 0), (trigger.Calls, app.Cpi.Requests));
        var (view, _) = await app.InvokeAsync(app.AdultA, new GetCpiRunsQuery(null), TestContext.Current.CancellationToken);
        Assert.Empty(((CpiRunPage)view!).Items);
    }

    [Fact]
    public async Task A_failed_fetch_is_a_failed_run_with_nothing_stored()
    {
        await using var app = await StartAsync(fixture);
        app.Cpi.FailWith = InflationCodes.SourceUnavailable;

        var run = await CollectAsync(app);

        Assert.Equal(("failed", 0, 0, 0), Counts(run));
        Assert.False(string.IsNullOrEmpty(run.Message));
        Assert.Empty(await StoredAsync(app));
    }

    [Fact]
    public async Task A_second_request_while_running_is_refused()
    {
        var trigger = new RecordingTrigger();
        await using var app = await StartAsync(fixture, services => services.AddSingleton<ICpiJobTrigger>(trigger));
        Serve(app);

        var (requested, failure) = await app.InvokeAsync(app.AdultA, new RequestCpiSyncCommand(), TestContext.Current.CancellationToken);
        Assert.Null(failure);
        Assert.Equal(new CpiSyncRequested(true), requested);

        await using (var scope = Scope(app))
            await scope.ServiceProvider.GetRequiredService<ICpiRunLog>().TryStartAsync(TestContext.Current.CancellationToken);
        var (_, running) = await app.InvokeAsync(app.AdultA, new RequestCpiSyncCommand(), TestContext.Current.CancellationToken);
        var (_, collecting) = await app.InvokeAsync(app.AdultA, new CollectCpiCommand(), TestContext.Current.CancellationToken);
        Assert.Equal((InflationCodes.SyncRunning, InflationCodes.SyncRunning), (running?.Code, collecting?.Code));
        Assert.Equal(1, trigger.Calls);
    }

    [Fact]
    public async Task Without_jobs_a_sync_request_says_so()
    {
        await using var app = await StartAsync(fixture);

        var (_, failure) = await app.InvokeAsync(app.AdultA, new RequestCpiSyncCommand(), TestContext.Current.CancellationToken);

        Assert.Equal(InflationCodes.JobsDisabled, failure?.Code);
    }

    [Fact]
    public async Task Manual_value_is_stored_and_refused_on_an_insee_month()
    {
        await using var app = await StartAsync(fixture);

        var (view, failure) = await app.InvokeAsync(app.AdultA, new SetCpiValueCommand(2026, 9, 103.1m), TestContext.Current.CancellationToken);
        Assert.Null(failure);
        Assert.Equal(new CpiValueView("2026-09", "103.10", null, Today, "manual"), view);

        Serve(app);
        await CollectAsync(app);
        var (_, fromInsee) = await app.InvokeAsync(app.AdultA, new SetCpiValueCommand(2026, 9, 103.2m), TestContext.Current.CancellationToken);
        Assert.Equal(InflationCodes.FromInsee, fromInsee?.Code);
        Assert.Equal(103.07m, (await StoredAsync(app)).Single(v => v.Month == new YearMonth(2026, 9)).Value);
    }

    [Theory]
    [InlineData(2026, 13, 103.0, "month")]
    [InlineData(2026, 10, 103.0, "month")] // not over yet
    [InlineData(2027, 1, 103.0, "month")]
    [InlineData(1995, 12, 60.0, "month")]
    [InlineData(2026, 9, 0.0, "value")]
    [InlineData(2026, 9, -3.0, "value")]
    [InlineData(2026, 9, 103.12345, "value")] // four decimals at most
    [InlineData(2026, 9, 120.0, "value")] // more than 3 % from the previous stored month
    public async Task Manual_value_is_validated(int year, int month, double value, string field)
    {
        await using var app = await StartAsync(fixture);
        await Put(app, new CpiValue(new YearMonth(2026, 8), 103.00m, null, Today, "manual"));

        var (_, failure) = await app.InvokeAsync(app.AdultA, new SetCpiValueCommand(year, month, (decimal)value), TestContext.Current.CancellationToken);

        Assert.Equal((InflationCodes.InvalidValue, field), (failure?.Code, failure?.Metadata?["field"]));
        Assert.Single(await StoredAsync(app));
    }

    [Fact]
    public async Task Manual_value_of_a_million_or_more_is_refused_even_without_a_previous_month()
    {
        await using var app = await StartAsync(fixture);

        var (_, failure) = await app.InvokeAsync(app.AdultA, new SetCpiValueCommand(2026, 9, 1_000_000m), TestContext.Current.CancellationToken);

        Assert.Equal((InflationCodes.InvalidValue, "value"), (failure?.Code, failure?.Metadata?["field"]));
        Assert.Empty(await StoredAsync(app));
    }

    [Fact]
    public async Task Inflation_reads_the_stored_series()
    {
        await using var app = await StartAsync(fixture);
        Serve(app);
        await CollectAsync(app);
        await using var scope = Scope(app);

        var series = await scope.ServiceProvider.GetRequiredService<IInflation>().ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new YearMonth(2026, 9), series.LastPublished);
        Assert.Equal(103.07m, series.Index(new YearMonth(2026, 9)));
    }

    [Fact]
    public async Task Get_inflation_gives_ten_years_and_the_last_twelve_months()
    {
        await using var app = await StartAsync(fixture);
        Serve(app);
        await CollectAsync(app);

        var (view, failure) = await app.InvokeAsync(app.AdultB, new GetInflationQuery(), TestContext.Current.CancellationToken);

        Assert.Null(failure);
        var inflation = (InflationView)view!;
        Assert.Equal("2026-09", inflation.LastPublished);
        Assert.Equal("2026-09", inflation.Last12Months!.To);
        Assert.Equal("3.1", inflation.Last12Months.Percent); // 103.07 / CPI(2025-09) - 1
        Assert.Equal("0.9", inflation.Years[0].Percent); // mean(2025) / mean(2024) - 1
        Assert.Equal(10, inflation.Years.Count);
        Assert.Equal(2025, inflation.Years[0].Year);
        Assert.Equal(2016, inflation.Years[^1].Year);
        Assert.All(inflation.Years, y => Assert.Matches(@"^-?\d+\.\d$", y.Percent));
    }

    [Theory]
    [InlineData("-0.0004", "0.0")]
    [InlineData("0.0004", "0.0")]
    [InlineData("0.0215", "2.2")] // half away from zero
    [InlineData("-0.0125", "-1.3")]
    public void Percent_has_one_decimal(string fraction, string expected)
        => Assert.Equal(expected, CpiViews.Percent(decimal.Parse(fraction, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public async Task Get_inflation_on_an_empty_series()
    {
        await using var app = await StartAsync(fixture);

        var (view, failure) = await app.InvokeAsync(app.AdultB, new GetInflationQuery(), TestContext.Current.CancellationToken);

        Assert.Null(failure);
        var inflation = (InflationView)view!;
        Assert.Null(inflation.LastPublished);
        Assert.Null(inflation.Last12Months);
        Assert.Empty(inflation.Years);
    }
}
