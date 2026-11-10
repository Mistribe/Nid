using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Application.Fx;
using Nid.Core.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Core.IntegrationTests.Fx;

public class FxRequestTests(PostgresFixture fixture, SharedNidApp shared) : IClassFixture<PostgresFixture>, IClassFixture<SharedNidApp>
{
    private sealed class RecordingTrigger : IFxJobTrigger
    {
        public int Calls;
        public Task<Result> TriggerBackfillAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Result.Success());
        }
    }

    private static readonly LocalDate Day = new(2025, 10, 1);

    [Fact]
    public async Task A_manual_rate_is_stored_and_read_back()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var (set, failure) = await app.InvokeAsync(app.AdultA, new SetManualFxRateCommand(Day, "EUR", "0.9123456789"), TestContext.Current.CancellationToken);
        Assert.Null(failure);
        Assert.Equal(new FxRateView(Day, "EUR", "0.9123456789", "manual"), set);

        var (read, _) = await app.InvokeAsync(app.AdultB, new GetFxRatesQuery("EUR", Day, Day), TestContext.Current.CancellationToken);
        Assert.Equal("0.9123456789", Assert.Single((IReadOnlyList<FxRateView>)read!).Rate);
    }

    [Theory]
    [InlineData("EUR", "0")]
    [InlineData("EUR", "-1")]
    [InlineData("EUR", "abc")]
    [InlineData("EUR", "1.12345678901")]   // eleven decimals
    [InlineData("EUR", "1000000000")]      // ten integer digits
    [InlineData("USD", "1")]
    [InlineData("eur", "1")]
    public async Task A_manual_rate_is_refused_when_invalid(string quote, string rate)
    {
        var app = shared.App;
        var (_, failure) = await app.InvokeAsync(app.AdultA, new SetManualFxRateCommand(Day, quote, rate), TestContext.Current.CancellationToken);
        Assert.Equal(FxCodes.InvalidRate, failure?.Code);
    }

    [Fact]
    public async Task A_manual_rate_in_the_future_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (_, failure) = await app.InvokeAsync(app.AdultA, new SetManualFxRateCommand(NidApp.GoldenCaseToday.PlusDays(1), "EUR", "0.9"), TestContext.Current.CancellationToken);
        Assert.Equal(FxCodes.InvalidRate, failure?.Code);
    }

    [Theory]
    [InlineData("2025-01-01", "2026-02-05")]  // 401 days
    [InlineData("2025-02-01", "2025-01-01")]  // reversed
    public async Task A_rate_range_is_bounded(string from, string to)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (_, failure) = await app.InvokeAsync(app.AdultA,
            new GetFxRatesQuery("EUR", NodaTime.Text.LocalDatePattern.Iso.Parse(from).Value, NodaTime.Text.LocalDatePattern.Iso.Parse(to).Value), TestContext.Current.CancellationToken);
        Assert.Equal(FxCodes.InvalidRange, failure?.Code);
    }

    [Fact]
    public async Task Without_jobs_a_sync_request_says_so()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (_, failure) = await app.InvokeAsync(app.AdultA, new RequestFxSyncCommand(), TestContext.Current.CancellationToken);
        Assert.Equal(FxCodes.JobsDisabled, failure?.Code);
    }

    [Fact]
    public async Task A_sync_request_triggers_the_backfill_unless_a_run_is_in_progress()
    {
        var trigger = new RecordingTrigger();
        await using var app = await NidApp.StartAsync(fixture, services => services.AddSingleton<IFxJobTrigger>(trigger));

        var (requested, failure) = await app.InvokeAsync(app.AdultA, new RequestFxSyncCommand(), TestContext.Current.CancellationToken);
        Assert.Null(failure);
        Assert.Equal(new FxSyncRequested(true), requested);
        Assert.Equal(1, trigger.Calls);

        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IFxRunLog>().TryStartAsync(FxRunKind.Daily, TestContext.Current.CancellationToken);

        var (_, running) = await app.InvokeAsync(app.AdultA, new RequestFxSyncCommand(), TestContext.Current.CancellationToken);
        Assert.Equal(FxCodes.SyncRunning, running?.Code);
        Assert.Equal(1, trigger.Calls);
    }

    [Fact]
    public async Task Collection_commands_run_offline_in_tests_and_journal_their_failure()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var (run, failure) = await app.InvokeAsync(app.AdultA, new BackfillFxRatesCommand(), TestContext.Current.CancellationToken);

        Assert.Null(failure);
        Assert.Equal(("failed", FxCodes.SourceUnavailable), (((FxSyncRunView)run!).Status, ((FxSyncRunView)run).FailureCode));
        var (runs, _) = await app.InvokeAsync(app.AdultA, new GetFxSyncRunsQuery(null), TestContext.Current.CancellationToken);
        Assert.Single(((FxSyncRunPage)runs!).Items);
    }
}
