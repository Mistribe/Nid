using Nid.Core.Application.Series;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;

namespace Nid.Core.Tests;

public sealed class FxSeriesTests
{
    private static readonly PersonId Person = PersonId.New();
    private static readonly Currency Gbp = Code("GBP");

    private static Currency Code(string code) => Currency.TryCreate(code, out var c) ? c : throw new ArgumentException(code);

    private static LocalDate D(int y, int m, int d) => new(y, m, d);

    private sealed class FakeFxRateStore(IReadOnlyList<FxRate> rates) : IFxRateStore
    {
        public Task<int> WriteAsync(IReadOnlyList<FxRate> rates, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<FxRate>> ReadAsync(IReadOnlyCollection<Currency> quotes, LocalDate from, LocalDate to, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FxRate>>(rates.Where(r => quotes.Contains(r.Quote) && r.OnDate >= from && r.OnDate <= to).OrderBy(r => r.OnDate).ToList());

        public Task<IReadOnlyDictionary<Currency, decimal>> PreviousAsync(IReadOnlyCollection<Currency> quotes, LocalDate date, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<FxQuoteSpan>> QuotesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FxQuoteSpan>>(rates.GroupBy(r => r.Quote).OrderBy(g => g.Key.Code)
                .Select(g => new FxQuoteSpan(g.Key, g.Min(r => r.OnDate), g.Max(r => r.OnDate))).ToList());

        public Task<IReadOnlySet<LocalDate>> DatesFromAsync(FxRateSource source, LocalDate from, CancellationToken ct) => throw new NotSupportedException();
    }

    private static FxSeries Series() => new(new FakeFxRateStore([
        new(D(2025, 10, 1), Currency.Eur, 0.85m, FxRateSource.Ecb),
        new(D(2025, 10, 2), Currency.Eur, 0.86m, FxRateSource.Ecb),
        new(D(2025, 10, 1), Gbp, 0.75m, FxRateSource.Ecb)]));

    private static Task<SeriesPoints?> Points(string key, LocalDate? from = null)
        => Series().PointsAsync(Person, VisibilityScope.Household, key, from, null, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Lists_one_series_per_stored_quote()
    {
        var listed = await Series().ListAsync(Person, VisibilityScope.Household, null, TestContext.Current.CancellationToken);

        Assert.Equal(["fx:EUR", "fx:GBP"], listed.Select(i => i.Key));
        Assert.Equal(new SeriesInfo("fx:EUR", SeriesKinds.Fx, "EUR", "rate:EUR", "daily", D(2025, 10, 1), D(2025, 10, 2), null), listed[0]);
    }

    [Fact]
    public async Task Search_matches_the_code()
        => Assert.Equal(["fx:EUR"], (await Series().ListAsync(Person, VisibilityScope.Household, "eu", TestContext.Current.CancellationToken)).Select(i => i.Key));

    [Fact]
    public async Task Search_matches_the_key()
        => Assert.Equal(["fx:EUR", "fx:GBP"], (await Series().ListAsync(Person, VisibilityScope.Household, "fx", TestContext.Current.CancellationToken)).Select(i => i.Key));

    [Fact]
    public async Task Points_read_the_stored_rates()
        => Assert.Equal([new SeriesPoint(D(2025, 10, 1), 0.85m), new SeriesPoint(D(2025, 10, 2), 0.86m)], (await Points("fx:EUR"))!.Points);

    [Fact]
    public async Task A_stored_quote_without_rate_in_the_period_is_empty()
        => Assert.Empty((await Points("fx:GBP", D(2025, 10, 2)))!.Points);

    [Theory]
    [InlineData("fx:JPY")]
    [InlineData("fx:eur")]
    [InlineData("fx:USD")]
    [InlineData("fx")]
    public async Task An_unknown_or_malformed_quote_is_not_found(string key)
        => Assert.Null(await Points(key));
}
