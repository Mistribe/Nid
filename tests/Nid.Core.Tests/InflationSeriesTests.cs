using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Tests;

public class InflationSeriesTests
{
    private static readonly YearMonth Start = new(2024, 1);

    private static decimal Idx(int n) => 100m * (decimal)Math.Pow(1.002, n);

    // 100 x 1.002^n from 2024-01 to 2026-09 (33 months), minus the months named.
    private static InflationSeries Series(params YearMonth[] without)
    {
        var d = new Dictionary<YearMonth, decimal>();
        for (var n = 0; n < 33; n++)
        {
            var m = Start.PlusMonths(n);
            if (!without.Contains(m)) d[m] = Idx(n);
        }
        return new InflationSeries(d);
    }

    [Fact]
    public void Between_is_the_ratio_of_the_two_months() =>
        Assert.Equal(Idx(12) / Idx(0), Series().Between(new YearMonth(2024, 1), new YearMonth(2025, 1)));

    [Fact]
    public void Between_is_null_when_a_month_is_missing() =>
        Assert.Null(Series().Between(new YearMonth(2023, 12), new YearMonth(2025, 1)));

    [Fact]
    public void Last12Months_ends_at_the_last_stored_month()
    {
        var last = Series().Last12Months!.Value;
        Assert.Equal(new YearMonth(2026, 9), last.To);
        Assert.Equal(Idx(32) / Idx(20) - 1, last.Rate);
    }

    [Fact]
    public void Last12Months_is_null_without_thirteen_months() =>
        Assert.Null(new InflationSeries(new Dictionary<YearMonth, decimal> { [Start] = 100m, [Start.PlusMonths(1)] = 101m }).Last12Months);

    [Fact]
    public void Last12Months_is_null_when_the_month_a_year_before_is_absent()
    {
        var d = new Dictionary<YearMonth, decimal>();
        for (var n = 0; n < 24; n++) d[Start.PlusMonths(n)] = Idx(n);
        d.Remove(Start.PlusMonths(23 - 12));
        Assert.Null(new InflationSeries(d).Last12Months);
    }

    [Fact]
    public void A_later_change_to_the_source_does_not_move_the_series()
    {
        var d = new Dictionary<YearMonth, decimal> { [Start] = 100m };
        var s = new InflationSeries(d);
        d[Start] = 200m;
        Assert.Equal(100m, s.Index(Start));
    }

    [Fact]
    public void Annual_is_the_ratio_of_yearly_means()
    {
        var mean25 = Enumerable.Range(12, 12).Average(Idx);
        var mean24 = Enumerable.Range(0, 12).Average(Idx);
        Assert.Equal((double)(mean25 / mean24 - 1), (double)Series().Annual(2025)!.Value, 9);
    }

    [Fact]
    public void Annual_is_null_when_a_month_is_missing()
    {
        var s = Series(new YearMonth(2024, 6));
        Assert.Null(s.Annual(2024));
        Assert.Null(s.Annual(2025));
        Assert.NotNull(s.Between(new YearMonth(2024, 1), new YearMonth(2024, 12)));
    }

    [Fact]
    public void Empty_knows_nothing()
    {
        Assert.Null(InflationSeries.Empty.LastPublished);
        Assert.Null(InflationSeries.Empty.Last12Months);
        Assert.Null(InflationSeries.Empty.Annual(2025));
    }

    [Fact]
    public void Real_deflates_a_nominal_change() =>
        Assert.Equal(0.02m, Math.Round(InflationSeries.Real(0.0404m, 0.02m), 4));
}
