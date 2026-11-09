using Nid.Core.Application.Series;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;

namespace Nid.Core.Tests;

public sealed class ProjectionRateSeriesTests
{
    private static readonly PersonId Person = PersonId.New();
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private sealed class FakeHousehold(Currency currency, ProjectionAssumptions assumptions) : IHouseholdSettings
    {
        public Task<Currency> ReferenceCurrencyAsync(CancellationToken ct) => Task.FromResult(currency);
        public Task<ProjectionAssumptions> ProjectionAssumptionsAsync(CancellationToken ct) => Task.FromResult(assumptions);
        public Task<HouseholdSettingsInfo> ReadAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<UnambitiousFx.Functional.Result<HouseholdSettingsInfo>> UpdateAsync(HouseholdSettingsChange change, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, decimal>> AllocationTargetAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task SaveAllocationTargetAsync(IReadOnlyDictionary<string, decimal> target, CancellationToken ct) => throw new NotSupportedException();
        public Task SaveProjectionAssumptionsAsync(ProjectionAssumptions a, CancellationToken ct) => throw new NotSupportedException();
        public Task<FeeReferences?> FeeReferencesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task SaveFeeReferencesAsync(FeeReferences references, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeCalendar : IHouseholdCalendar
    {
        public Task<LocalDate> TodayAsync(CancellationToken ct) => Task.FromResult(new LocalDate(2025, 10, 15));
        public Task<LocalDateTime> NowAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<DateTimeZone> ZoneAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    // 100 → 102.4 over twelve months: the observed change is 2.4 %.
    private sealed class FakeInflation : IInflation
    {
        public Task<InflationSeries> ReadAsync(CancellationToken ct) => Task.FromResult(new InflationSeries(
            Enumerable.Range(0, 13).ToDictionary(i => new YearMonth(2024, 9).PlusMonths(i), i => i == 0 ? 100m : 102.4m)));
    }

    private static readonly ProjectionAssumptions Assumptions = ProjectionDefaults.Assumptions with
    {
        ClassRatesByYear = new Dictionary<string, IReadOnlyDictionary<int, decimal>>
        {
            [AllocationClasses.MoneyMarket] = new Dictionary<int, decimal> { [2026] = 0.03m },
        },
    };

    private static ProjectionRateSeries Series(Currency? currency = null)
        => new(new FakeHousehold(currency ?? Currency.Eur, Assumptions), new FakeCalendar(), new FakeInflation());

    private static string[] Values(SeriesPoints? hit) => hit!.Points.Select(p => SeriesText.Of(p.Value)).ToArray();

    [Fact]
    public async Task A_class_without_typed_years_is_flat_at_its_long_term_rate()
    {
        var hit = await Series().PointsAsync(Person, VisibilityScope.Household, "projection:rate:equity", null, null, Ct);

        Assert.Equal(Enumerable.Repeat("6", 11), Values(hit));
        Assert.Equal((SeriesKinds.ProjectionRate, "equity", SeriesUnits.Percent), (hit!.Info.Kind, hit.Info.Name, hit.Info.Unit));
    }

    [Fact]
    public async Task A_typed_year_replaces_the_long_term_rate_for_that_year_only()
    {
        var hit = await Series().PointsAsync(Person, VisibilityScope.Household, "projection:rate:money_market", null, null, Ct);

        Assert.Equal(["2.5", "3", .. Enumerable.Repeat("2.5", 9)], Values(hit));
    }

    [Fact]
    public async Task A_non_euro_household_has_no_observed_rate_so_the_current_year_uses_the_long_term_one()
    {
        var usd = Currency.TryCreate("USD", out var c) ? c : throw new InvalidOperationException();

        var eur = await Series().PointsAsync(Person, VisibilityScope.Household, "projection:inflation", null, null, Ct);
        var other = await Series(usd).PointsAsync(Person, VisibilityScope.Household, "projection:inflation", null, null, Ct);

        Assert.Equal("2.4", Values(eur)[0]);
        Assert.Equal(Enumerable.Repeat("2", 11), Values(other));
    }

    [Fact]
    public async Task From_and_to_keep_the_years_inside()
    {
        var hit = await Series().PointsAsync(Person, VisibilityScope.Household, "projection:rate:equity",
            new LocalDate(2027, 1, 1), new LocalDate(2029, 6, 1), Ct);

        Assert.Equal([new LocalDate(2027, 1, 1), new LocalDate(2028, 1, 1), new LocalDate(2029, 1, 1)], hit!.Points.Select(p => p.Date));
    }

    [Theory]
    [InlineData("projection:rate:nope")]
    [InlineData("projection:rate:")]
    [InlineData("cpi")]
    public async Task A_key_that_is_not_a_projection_series_is_null(string key)
        => Assert.Null(await Series().PointsAsync(Person, VisibilityScope.Household, key, null, null, Ct));

    [Fact]
    public async Task The_nine_series_match_their_key_or_class()
    {
        var all = await Series().ListAsync(Person, VisibilityScope.Household, null, [], Ct);

        Assert.Equal(9, all.Count);
        Assert.All(all, i => Assert.Equal((new LocalDate(2025, 1, 1), new LocalDate(2035, 1, 1)), (i.First!.Value, i.Last!.Value)));
        Assert.Equal(["projection:rate:equity"], (await Series().ListAsync(Person, VisibilityScope.Household, "equity", [], Ct)).Select(i => i.Key));
        Assert.Equal(["projection:inflation"], (await Series().ListAsync(Person, VisibilityScope.Household, "inflation", [], Ct)).Select(i => i.Key));
        Assert.Equal(9, (await Series().ListAsync(Person, VisibilityScope.Household, "projection", [], Ct)).Count);
        Assert.Equal(9, (await Series().ListAsync(Person, VisibilityScope.Household, "  ", [], Ct)).Count);
        Assert.Equal(["projection:inflation"], (await Series().ListAsync(Person, VisibilityScope.Household, "zzz", [SeriesKinds.ProjectionInflation], Ct)).Select(i => i.Key));
        Assert.Equal(8, (await Series().ListAsync(Person, VisibilityScope.Household, "zzz", [SeriesKinds.ProjectionRate], Ct)).Count);
    }
}
