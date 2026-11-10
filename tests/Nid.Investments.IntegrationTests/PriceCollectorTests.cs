using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Application.Admin;
using Nid.Core.Contracts;
using Nid.Investments.Application.Prices;
using Nid.Investments.Contracts;
using Nid.Investments.Infrastructure.Persistence;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Investments.IntegrationTests;

/// <summary>Holding prices spec §3–§4: the nightly collection against the recorded pages of <c>tests/vectors/prices/</c>.</summary>
public class PriceCollectorTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Fund = "FR0010148981", Etf = "LU1681043599";
    private const string FundPath = "/bourse/opcvm/cours/MP-800128/";
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 23, 21, 0);

    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    private sealed class RecordingTrigger : IPriceJobTrigger
    {
        public int Calls;
        public Task<Result> TriggerAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Result.Success());
        }
    }

    private static byte[] Vector(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "vectors", "prices", name));

    private static Task<NidApp> StartAsync(PostgresFixture fixture, Action<IServiceCollection>? more = null)
        => NidApp.StartAsync(fixture, services =>
        {
            services.AddSingleton<IClock>(new FixedClock(Now));
            services.AddSingleton(new PriceOptions { RequestSpacing = TimeSpan.Zero });
            more?.Invoke(services);
        });

    /// <summary>Every request answers from the vectors; nothing is unavailable.</summary>
    private static void Serve(NidApp app, params (string Key, string Vector)[] responses)
    {
        app.Prices.FailWith = null;
        foreach (var (key, vector) in responses)
            app.Prices.Responses[key] = Vector(vector);
    }

    private static async Task EnableAsync(NidApp app)
    {
        var (_, failure) = await app.InvokeAsync(app.AdultA, new UpdateHouseholdSettingsCommand(null, null, true));
        Assert.Null(failure);
    }

    private static AsyncServiceScope Scope(NidApp app)
    {
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }

    /// <summary>A holding of <paramref name="isin"/> in a private envelope of B, which A (the admin running the collection) cannot see.</summary>
    private static async Task<(ItemId Envelope, Guid Holding)> HoldAsync(NidApp app, string isin, string currency = "EUR")
    {
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultB, Visibility.Private, currency);
        var holding = Guid.NewGuid();
        await using var scope = Scope(app);
        await scope.ServiceProvider.GetRequiredService<IHoldingStore>().AddAsync(
            new EnvelopeHolding(holding, envelope, "Support " + isin, isin, "fund", null, null, null, null, null, null, null, null), default);
        return (envelope, holding);
    }

    private static async Task<PriceRunView> CollectAsync(NidApp app)
    {
        var (run, failure) = await app.InvokeAsync(app.AdultA, new CollectPricesCommand());
        Assert.Null(failure);
        return (PriceRunView)run!;
    }

    private static async Task<IReadOnlyList<SecurityPrice>> AcceptedAsync(NidApp app, string isin)
    {
        await using var scope = Scope(app);
        return (await scope.ServiceProvider.GetRequiredService<ISecurityPriceStore>().LatestAcceptedAsync([isin], default)).Values.ToList();
    }

    private static async Task<SecurityListing?> ListingAsync(NidApp app, string isin)
    {
        await using var scope = Scope(app);
        return (await scope.ServiceProvider.GetRequiredService<ISecurityPriceStore>().ListingsAsync([isin], default)).GetValueOrDefault(isin);
    }

    private static async Task<SecurityPriceRow> StoredAsync(NidApp app, string isin, LocalDate on)
    {
        await using var db = PostgresFixture.NewInvestmentsContext(app.ConnectionString);
        return await db.SecurityPrices.SingleAsync(p => p.Isin == isin && p.OnDate == on);
    }

    private static (int, int, int, int, int) Counts(PriceRunView run) => (run.Queried, run.Accepted, run.Rejected, run.NotFound, run.SourceErrors);

    [Fact]
    public async Task Disabled_writes_no_run()
    {
        await using var app = await StartAsync(fixture);
        Serve(app, ($"boursorama-search:{Fund}", "boursorama-search-fund.html"));
        await HoldAsync(app, Fund);

        var (_, failure) = await app.InvokeAsync(app.AdultA, new CollectPricesCommand(), TestContext.Current.CancellationToken);

        Assert.Equal(PriceCodes.Disabled, failure?.Code);
        await using var scope = Scope(app);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<IPriceRunLog>().RecentAsync(5, TestContext.Current.CancellationToken));
        Assert.Empty(app.Prices.Requested);
    }

    [Fact]
    public async Task Collects_a_fund_from_boursorama()
    {
        await using var app = await StartAsync(fixture);
        Serve(app, ($"boursorama-search:{Fund}", "boursorama-search-fund.html"), ($"boursorama:{FundPath}", "boursorama-fund.html"));
        await HoldAsync(app, Fund);
        await EnableAsync(app);

        var run = await CollectAsync(app);

        Assert.Equal(("succeeded", (1, 1, 0, 0, 0)), (run.Status, Counts(run)));
        var price = Assert.Single(await AcceptedAsync(app, Fund));
        Assert.Equal((new LocalDate(2026, 10, 21), 3088.54m, Currency.Eur, PriceSources.Boursorama, (decimal?)null),
            (price.OnDate, price.Price, price.Currency, price.Source, price.Previous));
        Assert.Equal(FundPath, (await ListingAsync(app, Fund))?.BoursoramaPath);
        Assert.DoesNotContain(app.Prices.Requested, r => r.StartsWith("yahoo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Falls_back_to_yahoo()
    {
        await using var app = await StartAsync(fixture);
        Serve(app, ($"yahoo-search:{Etf}", "yahoo-search.json"), ("yahoo-chart:CW8.PA", "yahoo-chart.json"));
        app.Prices.Responses[$"boursorama-search:{Etf}"] = "<html></html>"u8.ToArray();
        await HoldAsync(app, Etf);
        await EnableAsync(app);

        var run = await CollectAsync(app);

        Assert.Equal((1, 1, 0, 0, 0), Counts(run));
        var price = Assert.Single(await AcceptedAsync(app, Etf));
        Assert.Equal((new LocalDate(2026, 10, 23), 706.0m, Currency.Eur, PriceSources.Yahoo), (price.OnDate, price.Price, price.Currency, price.Source));
        var listing = await ListingAsync(app, Etf);
        Assert.Equal((null, "CW8.PA"), (listing?.BoursoramaPath, listing?.YahooSymbol));
    }

    [Fact]
    public async Task A_boursorama_page_without_the_isin_falls_back_to_yahoo()
    {
        await using var app = await StartAsync(fixture);
        Serve(app, ($"boursorama-search:{Etf}", "boursorama-search-etf.html"), ("yahoo-search:" + Etf, "yahoo-search.json"), ("yahoo-chart:CW8.PA", "yahoo-chart.json"));
        app.Prices.Responses["boursorama:/bourse/trackers/cours/1rTCW8/"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Vector("boursorama-etf.html")).Replace(Etf, "XX0000000000"));
        await HoldAsync(app, Etf);
        await EnableAsync(app);

        var run = await CollectAsync(app);

        Assert.Equal((1, 1, 0, 0, 0), Counts(run));
        Assert.Equal(PriceSources.Yahoo, Assert.Single(await AcceptedAsync(app, Etf)).Source);
    }

    [Fact]
    public async Task An_unknown_isin_is_not_found()
    {
        await using var app = await StartAsync(fixture);
        Serve(app);
        await HoldAsync(app, Fund);
        await EnableAsync(app);

        Assert.Equal((1, 0, 0, 1, 0), Counts(await CollectAsync(app)));
        Assert.Equal(Now, (await ListingAsync(app, Fund))?.NotFoundAt);

        // Searched again at most once a week: the next night asks nothing.
        app.Prices.Requested.Clear();
        Assert.Equal((1, 0, 0, 1, 0), Counts(await CollectAsync(app)));
        Assert.Empty(app.Prices.Requested);
    }

    [Fact]
    public async Task A_malformed_page_is_a_source_error()
    {
        await using var app = await StartAsync(fixture);
        Serve(app, ($"boursorama-search:{Fund}", "boursorama-search-fund.html"));
        app.Prices.Responses[$"boursorama:{FundPath}"] = "<html></html>"u8.ToArray();
        await HoldAsync(app, Fund);
        await EnableAsync(app);

        Assert.Equal((1, 0, 0, 0, 1), Counts(await CollectAsync(app)));
        Assert.Empty(await AcceptedAsync(app, Fund));
        Assert.Contains($"yahoo-search:{Fund}", app.Prices.Requested);
    }

    [Fact]
    public async Task An_unavailable_source_is_a_source_error()
    {
        await using var app = await StartAsync(fixture);
        await HoldAsync(app, Fund);
        await EnableAsync(app);

        var run = await CollectAsync(app); // the test host's sources are offline

        Assert.Equal(("succeeded", (1, 0, 0, 0, 1)), (run.Status, Counts(run)));
        Assert.Null((await ListingAsync(app, Fund))?.NotFoundAt);
    }

    [Fact]
    public async Task A_currency_nid_does_not_know_is_a_source_error()
    {
        await using var app = await StartAsync(fixture);
        Serve(app, ($"yahoo-search:{Etf}", "yahoo-search.json"));
        // Yahoo quotes London lines in pence: stored as GBP, the price would be a hundred times too high.
        var pence = Encoding.UTF8.GetString(Vector("yahoo-chart.json")).Replace("\"currency\":\"EUR\"", "\"currency\":\"GBp\"");
        Assert.Contains("GBp", pence);
        app.Prices.Responses["yahoo-chart:CW8.PA"] = Encoding.UTF8.GetBytes(pence);
        await HoldAsync(app, Etf);
        await EnableAsync(app);

        Assert.Equal((1, 0, 0, 0, 1), Counts(await CollectAsync(app)));
        Assert.Empty(await AcceptedAsync(app, Etf));
    }

    [Fact]
    public async Task A_price_beyond_twenty_percent_is_rejected()
    {
        await using var app = await StartAsync(fixture);
        Serve(app, ($"boursorama-search:{Fund}", "boursorama-search-fund.html"), ($"boursorama:{FundPath}", "boursorama-fund.html"));
        await HoldAsync(app, Fund);
        await using (var scope = Scope(app))
            await scope.ServiceProvider.GetRequiredService<ISecurityPriceStore>().WriteAsync(new SecurityPrice(
                Fund, new LocalDate(2026, 10, 20), 2000m, Currency.Eur, PriceSources.Boursorama, PriceStatus.Accepted, null, Now, null), TestContext.Current.CancellationToken);
        await EnableAsync(app);

        Assert.Equal((1, 0, 1, 0, 0), Counts(await CollectAsync(app)));
        var stored = await StoredAsync(app, Fund, new LocalDate(2026, 10, 21));
        Assert.Equal(("rejected", 3088.54m, (decimal?)2000m), (stored.Status, stored.Price, stored.Previous));
    }

    [Fact]
    public async Task The_snapshot_price_is_the_first_reference()
    {
        await using var app = await StartAsync(fixture);
        Serve(app, ($"boursorama-search:{Fund}", "boursorama-search-fund.html"), ($"boursorama:{FundPath}", "boursorama-fund.html"));
        var (envelope, holding) = await HoldAsync(app, Fund);
        await SnapshotAsync(app, envelope, holding, 2000m);
        await EnableAsync(app);

        Assert.Equal((1, 0, 1, 0, 0), Counts(await CollectAsync(app)));
        Assert.Equal(2000m, (await StoredAsync(app, Fund, new LocalDate(2026, 10, 21))).Previous);
    }

    [Fact]
    public async Task A_snapshot_price_in_another_currency_is_converted()
    {
        await using var app = await StartAsync(fixture);
        Serve(app, ($"boursorama-search:{Fund}", "boursorama-search-fund.html"), ($"boursorama:{FundPath}", "boursorama-fund.html"));
        var (envelope, holding) = await HoldAsync(app, Fund, "USD");
        await SnapshotAsync(app, envelope, holding, 2000m);
        await using (var scope = Scope(app))
            await scope.ServiceProvider.GetRequiredService<IFxRateStore>().WriteAsync(
                [new FxRate(new LocalDate(2026, 9, 30), Currency.Eur, 1.5m, FxRateSource.Manual)], TestContext.Current.CancellationToken);
        await EnableAsync(app);

        // 2000 USD is 3000 EUR: 3088.54 EUR is within 20 %; unconverted, it would have been refused.
        Assert.Equal((1, 1, 0, 0, 0), Counts(await CollectAsync(app)));
    }

    [Fact]
    public async Task A_stale_cached_path_is_cleared()
    {
        await using var app = await StartAsync(fixture);
        Serve(app, ($"yahoo-search:{Etf}", "yahoo-search.json"), ("yahoo-chart:CW8.PA", "yahoo-chart.json"));
        await HoldAsync(app, Etf);
        await using (var scope = Scope(app))
            await scope.ServiceProvider.GetRequiredService<ISecurityPriceStore>().SaveListingAsync(
                new SecurityListing(Etf, "/bourse/trackers/cours/GONE/", null, Now - NodaTime.Duration.FromDays(30), null), TestContext.Current.CancellationToken);
        await EnableAsync(app);

        Assert.Equal((1, 1, 0, 0, 0), Counts(await CollectAsync(app)));
        Assert.Equal("boursorama:/bourse/trackers/cours/GONE/", app.Prices.Requested[0]);
        Assert.Contains($"yahoo-search:{Etf}", app.Prices.Requested);
        var listing = await ListingAsync(app, Etf);
        Assert.Equal((null, "CW8.PA"), (listing?.BoursoramaPath, listing?.YahooSymbol));
    }

    [Fact]
    public async Task Runs_carry_no_isin()
    {
        await using var app = await StartAsync(fixture);
        Serve(app, ($"boursorama-search:{Fund}", "boursorama-search-fund.html"), ($"boursorama:{FundPath}", "boursorama-fund.html"));
        await HoldAsync(app, Fund);
        await HoldAsync(app, Etf);
        await EnableAsync(app);
        await CollectAsync(app);

        var (runs, failure) = await app.InvokeAsync(app.AdultA, new GetPriceRunsQuery(null), TestContext.Current.CancellationToken);

        Assert.Null(failure);
        var json = JsonSerializer.Serialize(runs);
        Assert.Equal((2, 1, 0, 1, 0), Counts(Assert.Single(((PriceRunPage)runs!).Items)));
        Assert.DoesNotContain(Fund, json);
        Assert.DoesNotContain(Etf, json);
    }

    [Fact]
    public async Task RequestPriceSync()
    {
        var trigger = new RecordingTrigger();
        await using var app = await StartAsync(fixture, services => services.AddSingleton<IPriceJobTrigger>(trigger));

        var (_, notAdmin) = await app.InvokeAsync(app.AdultB, new RequestPriceSyncCommand(), TestContext.Current.CancellationToken);
        Assert.Equal(AdminCodes.Required, notAdmin?.Code);
        var (_, disabled) = await app.InvokeAsync(app.AdultA, new RequestPriceSyncCommand(), TestContext.Current.CancellationToken);
        Assert.Equal(PriceCodes.Disabled, disabled?.Code);
        Assert.Equal(0, trigger.Calls);

        await EnableAsync(app);
        var (requested, failure) = await app.InvokeAsync(app.AdultA, new RequestPriceSyncCommand(), TestContext.Current.CancellationToken);
        Assert.Null(failure);
        Assert.Equal(new PriceSyncRequested(true), requested);
        Assert.Equal(1, trigger.Calls);

        await using (var scope = Scope(app))
            await scope.ServiceProvider.GetRequiredService<IPriceRunLog>().TryStartAsync(TestContext.Current.CancellationToken);
        var (_, running) = await app.InvokeAsync(app.AdultA, new RequestPriceSyncCommand(), TestContext.Current.CancellationToken);
        Assert.Equal(PriceCodes.SyncRunning, running?.Code);
        var (_, collecting) = await app.InvokeAsync(app.AdultA, new CollectPricesCommand(), TestContext.Current.CancellationToken);
        Assert.Equal(PriceCodes.SyncRunning, collecting?.Code);
        Assert.Equal(1, trigger.Calls);
    }

    [Fact]
    public async Task Without_jobs_a_sync_request_says_so()
    {
        await using var app = await StartAsync(fixture);
        await EnableAsync(app);
        var (_, failure) = await app.InvokeAsync(app.AdultA, new RequestPriceSyncCommand(), TestContext.Current.CancellationToken);
        Assert.Equal(PriceCodes.JobsDisabled, failure?.Code);
    }

    private static async Task SnapshotAsync(NidApp app, ItemId envelope, Guid holding, decimal unitPrice)
    {
        await using var scope = Scope(app);
        await scope.ServiceProvider.GetRequiredService<IHoldingStore>().SaveSnapshotAsync(new HoldingSnapshot(
            Guid.NewGuid(), envelope, new LocalDate(2026, 9, 30), null, true, [new SnapshotLine(holding, 1m, unitPrice, unitPrice, null)]), default);
    }
}
