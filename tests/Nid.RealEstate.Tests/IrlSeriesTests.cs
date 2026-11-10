using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Application.Irl;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Tests;

public sealed class IrlSeriesTests
{
    private static readonly PersonId Person = PersonId.New();

    private sealed class FakeIrlStore(IReadOnlyList<IrlValue> values) : IIrlStore
    {
        public Task<IReadOnlyList<IrlValue>> ListAsync(CancellationToken ct) => Task.FromResult(values);
        public Task<(IReadOnlyList<IrlValue> Items, int Total)> PageAsync(int skip, int take, CancellationToken ct) => throw new NotSupportedException();
        public Task UpsertAsync(IrlValue value, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> UpsertManualAsync(IrlValue value, CancellationToken ct) => throw new NotSupportedException();
    }

    private static LocalDate D(int y, int m, int d) => new(y, m, d);

    private static IrlValue Value(int quarter, decimal value) => new(new IrlQuarter(2025, quarter), value, null, D(2025, 10, 20), "insee");

    private static IrlSeries Full() => new(new FakeIrlStore([Value(1, 140m), Value(2, 141.4m)]));

    private static Task<SeriesPoints?> Points(IrlSeries s, string key)
        => s.PointsAsync(Person, VisibilityScope.Household, key, null, null, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Quarters_are_dated_their_first_day()
    {
        var hit = await Points(Full(), "irl");

        Assert.Equal([new SeriesPoint(D(2025, 1, 1), 140m), new SeriesPoint(D(2025, 4, 1), 141.4m)], hit!.Points);
        Assert.Equal(SeriesFrequencies.Quarterly, hit.Info.Frequency);
    }

    [Fact]
    public async Task List_names_it_only_with_values_and_matches_the_key()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Empty(await new IrlSeries(new FakeIrlStore([])).ListAsync(Person, VisibilityScope.Household, null, [], ct));
        Assert.Equal("irl", Assert.Single(await Full().ListAsync(Person, VisibilityScope.Household, "irl", [], ct)).Key);
        Assert.Empty(await Full().ListAsync(Person, VisibilityScope.Household, "cpi", [], ct));
        Assert.Equal("irl", Assert.Single(await Full().ListAsync(Person, VisibilityScope.Household, "cpi", [SeriesKinds.Irl], ct)).Key);

        var listed = Assert.Single(await Full().ListAsync(Person, VisibilityScope.Household, null, [], ct));
        Assert.Equal(("irl", D(2025, 1, 1), D(2025, 4, 1)), (listed.Key, listed.First!.Value, listed.Last!.Value));
    }

    [Fact]
    public async Task Empty_store_answers_no_points_not_null()
        => Assert.Empty((await Points(new IrlSeries(new FakeIrlStore([])), "irl"))!.Points);

    [Fact]
    public async Task Other_keys_are_not_its_own()
    {
        Assert.Null(await Points(Full(), "irl:x"));
        Assert.Null(await Points(Full(), "cpi"));
    }
}
