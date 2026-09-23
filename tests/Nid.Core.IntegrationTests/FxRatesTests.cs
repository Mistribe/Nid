using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Core.IntegrationTests;

[Collection("postgres")]
public class FxRatesTests(PostgresFixture postgres)
{
    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    private static readonly LocalDate Today = new(2025, 10, 15);

    [Fact]
    public async Task Loads_the_gap_before_the_range_and_up_to_today()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var store = new FxRateStore(db);
        await store.WriteAsync(
        [
            new FxRate(new LocalDate(2025, 8, 25), Currency.Eur, 0.9m, FxRateSource.Ecb),  // 7 days before `from`
            new FxRate(new LocalDate(2025, 8, 24), Currency.Eur, 0.1m, FxRateSource.Ecb),  // 8 days: not loaded
            new FxRate(Today.PlusDays(-1), Currency.Eur, 0.8m, FxRateSource.Ecb),
        ], default);
        var rates = new FxRates(store, new FxOptions(), new FixedClock(Today.AtMidnight().InUtc().ToInstant()));

        var table = await rates.LoadAsync([Currency.Eur, Currency.Usd], new LocalDate(2025, 9, 1), new LocalDate(2025, 9, 2), default);

        Assert.True(table.TryConvert(1m, Currency.Usd, Currency.Eur, new LocalDate(2025, 9, 1), false, out var early, out _));
        Assert.Equal(0.9m, early);
        Assert.True(table.TryConvert(1m, Currency.Usd, Currency.Eur, Today.PlusYears(1), true, out var projected, out _));
        Assert.Equal(0.8m, projected);
        Assert.Equal(Today, table.Today);
    }

    [Fact]
    public async Task The_reference_currency_is_EUR_until_the_settings_exist_then_theirs()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var settings = new HouseholdSettings(db);
        Assert.Equal(Currency.Eur, await settings.ReferenceCurrencyAsync(default));

        db.HouseholdSettings.Add(new HouseholdSettingsRow { ReferenceCurrency = "CHF" });
        await db.SaveChangesAsync();

        Assert.Equal("CHF", (await settings.ReferenceCurrencyAsync(default)).Code);
    }
}
