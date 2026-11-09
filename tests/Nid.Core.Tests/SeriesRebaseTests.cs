using Nid.Core.Contracts;
using Nid.Core.Domain.Series;
using NodaTime;

namespace Nid.Core.Tests;

public sealed class SeriesRebaseTests
{
    private static LocalDate D(int y, int m, int d) => new(y, m, d);

    private static RebaseInput R(params (LocalDate, decimal)[] points) => new(true, points.Select(p => new SeriesPoint(p.Item1, p.Item2)).ToList());

    [Fact]
    public void One_series_is_based_on_its_first_point()
    {
        var r = SeriesRebase.Rebase([R((D(2025, 1, 1), 50m), (D(2025, 2, 1), 75m))]);
        Assert.Equal(D(2025, 1, 1), r.BaseDate);
        Assert.Equal<decimal?>([100m, 150m], r.Base100[0]);
    }

    [Fact]
    public void The_base_date_is_the_first_date_every_rebased_series_has_a_value()
    {
        var a = R((D(2024, 9, 1), 100m), (D(2025, 1, 1), 102.4m), (D(2025, 2, 1), 102.4m), (D(2025, 3, 1), 102.4m), (D(2025, 4, 1), 102.4m));
        var b = R((D(2025, 1, 1), 140m), (D(2025, 4, 1), 141.4m));
        var r = SeriesRebase.Rebase([a, b]);
        Assert.Equal(D(2025, 1, 1), r.BaseDate);
        Assert.Equal(97.66m, r.Base100[0][0]);
        Assert.Equal(100m, r.Base100[0][1]);
        Assert.Equal<decimal?>([100m, 101m], r.Base100[1]);
    }

    [Fact]
    public void Step_reading_takes_the_last_value_on_or_before_the_base_date()
    {
        var a = R((D(2025, 1, 1), 140m), (D(2025, 4, 1), 141.4m));
        var b = R((D(2025, 2, 15), 0.85m), (D(2025, 2, 16), 0.86m));
        var r = SeriesRebase.Rebase([a, b]);
        Assert.Equal(D(2025, 2, 15), r.BaseDate);
        Assert.Equal<decimal?>([100m, 101m], r.Base100[0]);
        Assert.Equal<decimal?>([100m, 101.18m], r.Base100[1]);
    }

    [Fact]
    public void A_series_without_points_is_left_out_of_the_base_date()
    {
        var r = SeriesRebase.Rebase([R((D(2025, 1, 1), 10m)), R()]);
        Assert.Equal(D(2025, 1, 1), r.BaseDate);
        Assert.Empty(r.Base100[1]);
    }

    [Fact]
    public void A_base_at_or_below_zero_gives_null_for_the_whole_series()
    {
        var r = SeriesRebase.Rebase([R((D(2025, 1, 1), 0m), (D(2025, 2, 1), 10m)), R((D(2025, 1, 1), -5m)), R((D(2025, 1, 1), 20m))]);
        Assert.All(r.Base100[0], Assert.Null);
        Assert.All(r.Base100[1], Assert.Null);
        Assert.Equal<decimal?>([100m], r.Base100[2]);
    }

    [Fact]
    public void A_percent_series_is_never_rebased_nor_moves_the_base_date()
    {
        var p = new RebaseInput(false, [new SeriesPoint(D(2026, 1, 1), 2m)]);
        var r = SeriesRebase.Rebase([p, R((D(2025, 1, 1), 10m))]);
        Assert.Equal(D(2025, 1, 1), r.BaseDate);
        Assert.Null(Assert.Single(r.Base100[0]));
        Assert.Equal<decimal?>([100m], r.Base100[1]);
    }

    [Fact]
    public void No_rebased_series_gives_no_base_date()
    {
        var r = SeriesRebase.Rebase([new RebaseInput(false, [new SeriesPoint(D(2026, 1, 1), 2m)])]);
        Assert.Null(r.BaseDate);
        Assert.Null(Assert.Single(r.Base100[0]));
    }
}
