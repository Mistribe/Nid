using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Tests;

public class RateTableTests
{
    private static readonly Currency Gbp = Code("GBP");
    private static readonly LocalDate Friday = new(2025, 9, 5);
    private static readonly LocalDate Today = new(2025, 10, 15);

    private static Currency Code(string code) => Currency.TryCreate(code, out var c) ? c : throw new ArgumentException(code);

    private static RateTable Table(params (LocalDate On, Currency Quote, decimal Rate)[] rates)
        => new(rates.Select(r => new FxRate(r.On, r.Quote, r.Rate, FxRateSource.Ecb)), Today, maxGapDays: 7);

    [Fact]
    public void The_same_currency_needs_no_rate()
    {
        Assert.True(RateTable.Empty(Today, 7).TryConvert(12.5m, Currency.Eur, Currency.Eur, Friday, false, out var converted, out _));
        Assert.Equal(12.5m, converted);
    }

    [Fact]
    public void Converts_from_USD_at_the_rate_of_the_date()
    {
        var table = Table((Friday, Currency.Eur, 0.9m));
        Assert.True(table.TryConvert(10m, Currency.Usd, Currency.Eur, Friday, false, out var converted, out _));
        Assert.Equal(9m, converted);
    }

    [Fact]
    public void A_weekend_uses_the_last_rate_before_it()
    {
        var table = Table((Friday, Currency.Eur, 0.9m), (Friday.PlusDays(3), Currency.Eur, 0.5m));
        Assert.True(table.TryConvert(10m, Currency.Usd, Currency.Eur, Friday.PlusDays(2), false, out var converted, out _));
        Assert.Equal(9m, converted);
    }

    [Fact]
    public void A_rate_seven_days_old_is_used_and_eight_days_old_is_missing()
    {
        var table = Table((Friday, Currency.Eur, 0.9m));
        Assert.True(table.TryConvert(10m, Currency.Usd, Currency.Eur, Friday.PlusDays(7), false, out _, out _));

        Assert.False(table.TryConvert(10m, Currency.Usd, Currency.Eur, Friday.PlusDays(8), false, out _, out var miss));
        Assert.Equal(new FxMiss(Currency.Eur, Friday.PlusDays(8)), miss);
    }

    [Fact]
    public void Before_the_first_rate_is_missing()
    {
        var table = Table((Friday, Currency.Eur, 0.9m));
        Assert.False(table.TryConvert(10m, Currency.Usd, Currency.Eur, Friday.PlusDays(-1), false, out _, out var miss));
        Assert.Equal(Currency.Eur, miss.Currency);
    }

    [Fact]
    public void Converts_into_USD_and_between_two_non_USD_currencies()
    {
        var table = Table((Friday, Currency.Eur, 0.8m), (Friday, Gbp, 0.6m));

        Assert.True(table.TryConvert(8m, Currency.Eur, Currency.Usd, Friday, false, out var usd, out _));
        Assert.Equal(10m, usd);

        Assert.True(table.TryConvert(8m, Currency.Eur, Gbp, Friday, false, out var gbp, out _));
        Assert.Equal(6m, gbp);
    }

    [Fact]
    public void A_projected_charge_uses_the_latest_rate()
    {
        var table = Table((Today.PlusDays(-1), Currency.Eur, 0.9m));
        Assert.True(table.TryConvert(10m, Currency.Usd, Currency.Eur, Today.PlusMonths(3), true, out var converted, out _));
        Assert.Equal(9m, converted);
    }

    [Fact]
    public void A_projected_charge_is_missing_when_the_latest_rate_is_too_old()
    {
        var table = Table((Today.PlusDays(-8), Currency.Eur, 0.9m));
        Assert.False(table.TryConvert(10m, Currency.Usd, Currency.Eur, Today.PlusMonths(3), true, out _, out var miss));
        Assert.Equal(new FxMiss(Currency.Eur, Today), miss);
    }
}
