using Nid.Core.Application.Series;
using Nid.Core.Contracts;
using Nid.Core.Domain.Inflation;
using Nid.Platform.Contracts;
using NodaTime;

namespace Nid.Core.Tests;

public sealed class CpiSeriesTests
{
    private static readonly PersonId Person = PersonId.New();
    private static readonly VisibilityScope Scope = VisibilityScope.Household;

    private sealed class FakeCpiStore(IReadOnlyList<CpiValue> values) : ICpiStore
    {
        public Task<IReadOnlyList<CpiValue>> ListAsync(CancellationToken ct) => Task.FromResult(values);
        public Task UpsertAsync(CpiValue value, CancellationToken ct) => throw new NotSupportedException();
        public Task UpsertManyAsync(IReadOnlyList<CpiValue> values, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> UpsertManualAsync(CpiValue value, CancellationToken ct) => throw new NotSupportedException();
    }

    private static LocalDate D(int y, int m, int d) => new(y, m, d);

    private static CpiValue Value(YearMonth month, decimal value) => new(month, value, null, D(2025, 10, 20), "insee");

    private static CpiSeries Full()
    {
        var values = new List<CpiValue> { Value(new YearMonth(2024, 9), 100m) };
        for (var m = new YearMonth(2024, 10); m <= new YearMonth(2025, 9); m = m.PlusMonths(1)) values.Add(Value(m, 102.4m));
        return new CpiSeries(new FakeCpiStore(values));
    }

    private static Task<SeriesPoints?> Points(CpiSeries s, string key, LocalDate? from = null, LocalDate? to = null)
        => s.PointsAsync(Person, Scope, key, from, to, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Cpi_points_are_dated_the_first_of_each_month()
    {
        var hit = await Points(Full(), "cpi");

        Assert.Equal(13, hit!.Points.Count);
        Assert.Equal(new SeriesPoint(D(2024, 9, 1), 100m), hit.Points[0]);
        Assert.Equal(new SeriesPoint(D(2025, 9, 1), 102.4m), hit.Points[^1]);
    }

    [Fact]
    public async Task Cpi_yoy_is_the_change_over_twelve_months_with_one_decimal()
    {
        var hit = await Points(Full(), "cpi:yoy");

        Assert.Equal(new SeriesPoint(D(2025, 9, 1), 2.4m), Assert.Single(hit!.Points));
        Assert.Equal(SeriesUnits.Percent, hit.Info.Unit);
    }

    [Fact]
    public async Task Points_honour_from_and_to()
        => Assert.Equal(3, (await Points(Full(), "cpi", D(2025, 1, 1), D(2025, 3, 31)))!.Points.Count);

    [Fact]
    public async Task Points_are_empty_not_null_before_any_collection()
        => Assert.Empty((await Points(new CpiSeries(new FakeCpiStore([])), "cpi"))!.Points);

    [Fact]
    public async Task List_omits_series_without_points_and_any_search()
    {
        Assert.Empty(await new CpiSeries(new FakeCpiStore([])).ListAsync(Person, Scope, null, TestContext.Current.CancellationToken));
        Assert.Empty(await Full().ListAsync(Person, Scope, "x", TestContext.Current.CancellationToken));

        var listed = await Full().ListAsync(Person, Scope, null, TestContext.Current.CancellationToken);
        Assert.Equal(["cpi", "cpi:yoy"], listed.Select(i => i.Key));
        Assert.Equal((D(2024, 9, 1), D(2025, 9, 1)), (listed[0].First!.Value, listed[0].Last!.Value));
        Assert.Equal((D(2025, 9, 1), D(2025, 9, 1)), (listed[1].First!.Value, listed[1].Last!.Value));
    }

    [Fact]
    public async Task Other_keys_are_not_its_own()
    {
        Assert.Null(await Points(Full(), "fx:EUR"));
        Assert.Null(await Points(Full(), "cpi:foo"));
    }
}
