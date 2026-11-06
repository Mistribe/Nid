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

    // 100 x 1.002^n from 1996-01 to 2026-09.
    private static InflationSeries Long()
    {
        var d = new Dictionary<YearMonth, decimal>();
        var m = new YearMonth(1996, 1);
        for (var n = 0; m <= new YearMonth(2026, 9); n++, m = m.PlusMonths(1)) d[m] = Idx(n);
        return new InflationSeries(d);
    }

    private static decimal LongIdx(int year, int month) => Idx((year - 1996) * 12 + month - 1);

    private static decimal Round1(decimal x) => Math.Round(x, 1, MidpointRounding.AwayFromZero);

    [Fact]
    public void Real_change_deflates_by_the_months_between()
    {
        var r = Long().RealChange(4m, new YearMonth(2024, 9), new YearMonth(2025, 9), Currency.Eur);
        Assert.Equal(Round1((1.04m / (LongIdx(2025, 9) / LongIdx(2024, 9)) - 1) * 100), r.Percent);
        Assert.Equal("2025-09", r.InflationTo);
        Assert.Null(r.Missing);
    }

    [Fact]
    public void Real_change_clamps_to_the_last_published_month()
    {
        var r = Long().RealChange(4m, new YearMonth(2025, 11), new YearMonth(2026, 11), Currency.Eur);
        Assert.Equal("2026-09", r.InflationTo);
        Assert.Equal(Round1((1.04m / (LongIdx(2026, 9) / LongIdx(2025, 11)) - 1) * 100), r.Percent);
    }

    [Fact]
    public void Real_change_outside_eur_is_currency() =>
        Assert.Equal(new RealPercentView(null, null, RealMissing.Currency),
            Long().RealChange(4m, new YearMonth(2024, 9), new YearMonth(2025, 9), Currency.Usd));

    [Fact]
    public void Real_change_on_an_empty_series_is_unavailable() =>
        Assert.Equal(new RealPercentView(null, null, RealMissing.Unavailable),
            InflationSeries.Empty.RealChange(4m, new YearMonth(2024, 9), new YearMonth(2025, 9), Currency.Eur));

    [Fact]
    public void Real_change_before_the_series_is_not_published() =>
        Assert.Equal(new RealPercentView(null, "2025-09", RealMissing.NotPublished),
            Long().RealChange(4m, new YearMonth(1995, 6), new YearMonth(2025, 9), Currency.Eur));

    [Fact]
    public void Real_change_without_a_nominal_change_is_none() =>
        Assert.Equal(RealPercentView.None, Long().RealChange(null, new YearMonth(2024, 9), new YearMonth(2025, 9), Currency.Usd));

    [Fact]
    public void Real_change_over_12_months_uses_the_last_12_months()
    {
        var r = Long().RealChangeOver12Months(4m, Currency.Eur);
        Assert.Equal("2026-09", r.InflationTo);
        Assert.Equal(Round1(InflationSeries.Real(0.04m, LongIdx(2026, 9) / LongIdx(2025, 9) - 1) * 100), r.Percent);
        Assert.Equal(RealMissing.NotPublished, new InflationSeries(new Dictionary<YearMonth, decimal> { [Start] = 100m }).RealChangeOver12Months(4m, Currency.Eur).Missing);
    }

    [Fact]
    public void Real_annual_return_matches_xirr_on_deflated_flows()
    {
        var at = new LocalDate(2025, 1, 15);
        var cash = new[] { (new LocalDate(2020, 1, 15), -1000m), (at, 1300m) };
        var deflated = new[]
        {
            (cash[0].Item1, -1000m * LongIdx(2025, 1) / LongIdx(2020, 1)),
            (at, 1300m),
        };
        var r = Long().RealAnnualReturn(cash, at, Currency.Eur);
        Assert.Equal(Xirr.AnnualPercent(deflated, at), r.Percent);
        Assert.Equal("2025-01", r.InflationTo);
    }

    [Fact]
    public void Real_annual_return_before_the_series_is_not_published()
    {
        var at = new LocalDate(2025, 1, 15);
        var r = Long().RealAnnualReturn([(new LocalDate(1990, 1, 1), -1000m), (at, 1300m)], at, Currency.Eur);
        Assert.Equal(RealMissing.NotPublished, r.Missing);
        Assert.Null(r.Percent);
    }

    [Fact]
    public void Quarter_mean_needs_three_months()
    {
        Assert.Equal((Idx(30) + Idx(31) + Idx(32)) / 3, Series().QuarterMean(2026, 3));
        Assert.Null(Series().QuarterMean(2026, 4));
    }

    [Fact]
    public void Real_annual_return_outside_eur_is_currency()
    {
        var at = new LocalDate(2025, 1, 15);
        Assert.Equal(new RealPercentView(null, null, RealMissing.Currency),
            Long().RealAnnualReturn([(new LocalDate(2020, 1, 15), -1000m), (at, 1300m)], at, Currency.Usd));
    }

    [Fact]
    public void Real_annual_return_on_an_empty_series_is_unavailable()
    {
        var at = new LocalDate(2025, 1, 15);
        Assert.Equal(new RealPercentView(null, null, RealMissing.Unavailable),
            InflationSeries.Empty.RealAnnualReturn([(new LocalDate(2020, 1, 15), -1000m), (at, 1300m)], at, Currency.Eur));
    }
}
