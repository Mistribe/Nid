using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Core.IntegrationTests;

[Collection("postgres")]
public class FxRateStoreTests(PostgresFixture postgres)
{
    private static readonly LocalDate Day = new(2025, 9, 1);
    private static readonly Currency Gbp = Code("GBP");

    private static Currency Code(string code) => Currency.TryCreate(code, out var c) ? c : throw new ArgumentException(code);

    private static FxRate Rate(Currency quote, decimal rate, FxRateSource source, LocalDate? on = null)
        => new(on ?? Day, quote, rate, source);

    private static async Task<FxRate> StoredAsync(FxRateStore store, Currency quote, LocalDate? on = null)
        => Assert.Single(await store.ReadAsync([quote], on ?? Day, on ?? Day, default));

    [Fact]
    public async Task Quotes_span_from_the_first_to_the_last_rate_per_quote()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var store = new FxRateStore(db);
        Assert.Empty(await store.QuotesAsync(TestContext.Current.CancellationToken));

        await store.WriteAsync([
            Rate(Currency.Eur, 0.9m, FxRateSource.Ecb, new LocalDate(2025, 9, 1)),
            Rate(Currency.Eur, 0.91m, FxRateSource.Ecb, new LocalDate(2025, 9, 3)),
            Rate(Gbp, 0.7m, FxRateSource.Ecb, new LocalDate(2025, 9, 2))], TestContext.Current.CancellationToken);

        Assert.Equal(
            [new FxQuoteSpan(Currency.Eur, new LocalDate(2025, 9, 1), new LocalDate(2025, 9, 3)), new FxQuoteSpan(Gbp, new LocalDate(2025, 9, 2), new LocalDate(2025, 9, 2))],
            await store.QuotesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_manual_rate_is_never_replaced_by_a_collected_one()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var store = new FxRateStore(db);

        await store.WriteAsync([Rate(Currency.Eur, 0.9m, FxRateSource.Manual)], TestContext.Current.CancellationToken);
        var written = await store.WriteAsync([Rate(Currency.Eur, 0.8m, FxRateSource.SubTrackerData), Rate(Gbp, 0.7m, FxRateSource.SubTrackerData)], TestContext.Current.CancellationToken);

        Assert.Equal(1, written); // GBP only
        var eur = await StoredAsync(store, Currency.Eur);
        Assert.Equal((0.9m, FxRateSource.Manual), (eur.Rate, eur.Source));
    }

    [Fact]
    public async Task The_ECB_only_fills_gaps_and_SubTracker_replaces_it()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var store = new FxRateStore(db);

        await store.WriteAsync([Rate(Currency.Eur, 0.8m, FxRateSource.SubTrackerData)], TestContext.Current.CancellationToken);
        Assert.Equal(0, await store.WriteAsync([Rate(Currency.Eur, 0.85m, FxRateSource.Ecb)], TestContext.Current.CancellationToken));
        Assert.Equal(0.8m, (await StoredAsync(store, Currency.Eur)).Rate);

        await store.WriteAsync([Rate(Gbp, 0.75m, FxRateSource.Ecb)], TestContext.Current.CancellationToken);
        await store.WriteAsync([Rate(Gbp, 0.74m, FxRateSource.SubTrackerData)], TestContext.Current.CancellationToken);
        var gbp = await StoredAsync(store, Gbp);
        Assert.Equal((0.74m, FxRateSource.SubTrackerData), (gbp.Rate, gbp.Source));
    }

    [Fact]
    public async Task The_same_source_replaces_its_own_row()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var store = new FxRateStore(db);

        await store.WriteAsync([Rate(Currency.Eur, 0.8m, FxRateSource.SubTrackerData)], TestContext.Current.CancellationToken);
        Assert.Equal(1, await store.WriteAsync([Rate(Currency.Eur, 0.81m, FxRateSource.SubTrackerData)], TestContext.Current.CancellationToken));
        Assert.Equal(0.81m, (await StoredAsync(store, Currency.Eur)).Rate);
    }

    [Fact]
    public async Task Previous_is_the_nearest_earlier_rate_of_each_quote()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var store = new FxRateStore(db);
        await store.WriteAsync(
        [
            Rate(Currency.Eur, 0.80m, FxRateSource.Ecb, Day.PlusDays(-10)),
            Rate(Currency.Eur, 0.82m, FxRateSource.Ecb, Day.PlusDays(-3)),
            Rate(Currency.Eur, 0.99m, FxRateSource.Ecb, Day),
        ], TestContext.Current.CancellationToken);

        var previous = await store.PreviousAsync([Currency.Eur, Gbp], Day, TestContext.Current.CancellationToken);

        Assert.Equal(0.82m, previous[Currency.Eur]);
        Assert.False(previous.ContainsKey(Gbp));
    }

    [Fact]
    public async Task Dates_of_a_source()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var store = new FxRateStore(db);

        await store.WriteAsync([Rate(Currency.Eur, 0.8m, FxRateSource.Ecb, Day.PlusDays(-1)), Rate(Currency.Eur, 0.8m, FxRateSource.SubTrackerData)], TestContext.Current.CancellationToken);

        Assert.Equal([Day], (await store.DatesFromAsync(FxRateSource.SubTrackerData, Day.PlusDays(-5), TestContext.Current.CancellationToken)).ToList());
    }

    [Fact]
    public async Task A_batch_larger_than_one_statement_is_written_whole()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var store = new FxRateStore(db);
        var rates = Enumerable.Range(0, FxRateStore.ChunkSize + 7)
            .Select(i => Rate(Currency.Eur, 0.8m, FxRateSource.Ecb, Day.PlusDays(-i))).ToList();

        Assert.Equal(rates.Count, await store.WriteAsync(rates, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_database_refuses_USD_and_a_rate_that_is_not_positive()
    {
        await using var db = await postgres.NewDatabaseAsync();
        db.FxRates.Add(new FxRateRow { OnDate = Day, Quote = "USD", Rate = 1m, Source = "ecb" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));

        db.ChangeTracker.Clear();
        db.FxRates.Add(new FxRateRow { OnDate = Day, Quote = "EUR", Rate = 0m, Source = "ecb" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }
}
