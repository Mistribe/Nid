using Nid.Core.Application.Dashboard;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Tests;

/// <summary>Issue #419: the net worth curve in euros of the last published month.</summary>
public class NetWorthHistoryRealTests
{
    private static readonly LocalDate Today = new(2025, 10, 15);
    private static readonly LocalDate Sept24 = new(2024, 9, 30);

    private static InflationSeries Series() => new(new Dictionary<YearMonth, decimal>
    {
        [new YearMonth(2024, 9)] = 100m,
        [new YearMonth(2025, 9)] = 102m,
    });

    private static NetWorthHistoryView Compose(Currency reference, InflationSeries series)
    {
        var grid = new[] { Sept24, new LocalDate(2025, 9, 30), Today };
        var parts = new[] { (IReadOnlyList<NetWorthPoint>)grid.Select(d => new NetWorthPoint(
            d, new Dictionary<Currency, decimal> { [reference] = 1000m }, new Dictionary<Currency, decimal> { [reference] = 800m }, 1)).ToList() };
        return NetWorthHistory.Compose(NetWorthPeriod.TwelveMonths, grid, parts, reference, RateTable.Empty(Today, 0), series);
    }

    [Fact]
    public void History_real_value_is_deflated_to_the_last_published_month()
    {
        var view = Compose(Currency.Eur, Series());

        Assert.Equal("2025-09", view.RealTo);
        Assert.Null(view.RealMissing);
        Assert.Equal(("1020.00", "816.00"), (view.Points[0].RealValue!.Amount, view.Points[0].RealContributions!.Amount));
        Assert.Equal("1000.00", view.Points[1].RealValue!.Amount);
    }

    [Fact]
    public void History_points_after_last_published_keep_their_nominal_value()
    {
        var today = Compose(Currency.Eur, Series()).Points[2];

        Assert.Equal(("1000.00", "800.00"), (today.RealValue!.Amount, today.RealContributions!.Amount));
    }

    [Fact]
    public void History_in_another_reference_currency_has_no_real_values()
    {
        var view = Compose(Currency.Usd, Series());

        Assert.Equal(RealMissing.Currency, view.RealMissing);
        Assert.Null(view.RealTo);
        Assert.All(view.Points, p => Assert.Null(p.RealValue));
    }

    [Fact]
    public void History_without_series_has_no_real_values()
    {
        var view = Compose(Currency.Eur, InflationSeries.Empty);

        Assert.Equal(RealMissing.Unavailable, view.RealMissing);
        Assert.All(view.Points, p => Assert.Null(p.RealValue));
    }
}
