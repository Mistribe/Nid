using Microsoft.Extensions.Logging.Abstractions;
using Nid.Core.Application.Series;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Core.Tests;

public sealed class SeriesUseCasesTests
{
    private static readonly PersonId Person = PersonId.New();
    private static readonly IActorAccessor Actor = new ActorAccessor { Current = Platform.Contracts.Actor.Browser(Person) };

    private static LocalDate D(int y, int m, int d) => new(y, m, d);

    private static SeriesInfo Info(string key, string kind = SeriesKinds.Cpi, string? name = null, string unit = SeriesUnits.Index, ItemId? item = null)
        => new(key, kind, name, unit, SeriesFrequencies.Monthly, null, null, item);

    private static SeriesPoints Owned(SeriesInfo info, params (LocalDate, decimal)[] points)
        => new(info, points.Select(p => new SeriesPoint(p.Item1, p.Item2)).ToList());

    private sealed class FakeContribution(string module, IReadOnlyList<SeriesPoints> owned, bool throws = false) : ISeriesContribution
    {
        public string Module => module;

        public Task<IReadOnlyList<SeriesInfo>> ListAsync(PersonId reader, VisibilityScope scope, string? q, IReadOnlyCollection<string> kinds, CancellationToken ct)
            => throws
                ? throw new InvalidOperationException("boom")
                : Task.FromResult<IReadOnlyList<SeriesInfo>>(owned.Select(o => o.Info).Where(i => SeriesSearch.Matches(i, q, kinds)).ToList());

        public Task<SeriesPoints?> PointsAsync(PersonId reader, VisibilityScope scope, string key, LocalDate? from, LocalDate? to, CancellationToken ct)
            => Task.FromResult(owned.FirstOrDefault(o => o.Info.Key == key) is { } hit
                ? hit with { Points = hit.Points.Where(p => (from is null || p.Date >= from) && (to is null || p.Date <= to)).ToList() }
                : null);
    }

    private static T Value<T>(Result<T> r) where T : notnull => r.TryGetValue(out var v) ? v : throw new InvalidOperationException(r.ToString());

    private static async Task<SeriesCatalogView> List(int page, params ISeriesContribution[] contributions)
        => Value(await new ListSeriesHandler(contributions, Actor, NullLogger<ListSeriesHandler>.Instance)
            .HandleAsync(new ListSeriesQuery(VisibilityScope.Household, null, page)));

    private static async Task<SeriesCatalogView> Search(string? q, string[]? kinds, params ISeriesContribution[] contributions)
        => Value(await new ListSeriesHandler(contributions, Actor, NullLogger<ListSeriesHandler>.Instance)
            .HandleAsync(new ListSeriesQuery(VisibilityScope.Household, q, 1, kinds)));

    private static async Task<Result<SeriesPointsView>> Points(string? s, params ISeriesContribution[] contributions)
        => await new GetSeriesPointsHandler(contributions, Actor).HandleAsync(new GetSeriesPointsQuery(VisibilityScope.Household, s, null, null));

    private static readonly SeriesPoints MonthlyCpi = Owned(Info("cpi"),
        (D(2024, 9, 1), 100m), (D(2025, 1, 1), 102.4m), (D(2025, 2, 1), 102.4m), (D(2025, 3, 1), 102.4m), (D(2025, 4, 1), 102.4m));

    private static readonly SeriesPoints QuarterlyIrl = Owned(Info("irl", SeriesKinds.Irl), (D(2025, 1, 1), 140m), (D(2025, 4, 1), 141.4m));

    [Fact]
    public async Task List_puts_reference_series_first_fx_last_among_them_then_items_by_module_and_name()
    {
        var property = ItemId.New();
        var income = ItemId.New();
        var core = new FakeContribution("core", [Owned(Info("fx:EUR", SeriesKinds.Fx)), Owned(Info("cpi")), Owned(Info("projection:inflation", SeriesKinds.ProjectionInflation))]);
        var realestate = new FakeContribution("realestate", [Owned(Info("irl", SeriesKinds.Irl)), Owned(Info($"property:{property.Value}:value", "property_value", "Appartement", item: property))]);
        var incomes = new FakeContribution("income", [Owned(Info($"income:{income.Value}", "income", "Bob salaire", item: income))]);

        var view = await List(1, realestate, incomes, core);

        Assert.Equal(["cpi", "irl", "projection:inflation", "fx:EUR", $"income:{income.Value}", $"property:{property.Value}:value"], view.Items.Select(i => i.Key));
        Assert.Equal([income, property], view.ExposedItems());
    }

    [Fact]
    public async Task List_pages_by_50_and_counts_the_total()
    {
        var fx = new FakeContribution("core", Enumerable.Range(0, 60).Select(i => Owned(Info($"fx:C{i:00}", SeriesKinds.Fx))).ToList());

        var first = await List(1, fx);
        Assert.Equal((50, 60, 1), (first.Items.Count, first.Total, first.Page));
        var second = await List(2, fx);
        Assert.Equal((10, 60, 2), (second.Items.Count, second.Total, second.Page));
        Assert.Equal(1, (await List(0, fx)).Page);
    }

    [Fact]
    public async Task List_matches_the_search_or_a_listed_kind()
    {
        var core = new FakeContribution("core", [Owned(Info("cpi")), Owned(Info("irl", SeriesKinds.Irl)), Owned(Info("fx:EUR", SeriesKinds.Fx, "EUR"))]);

        Assert.Equal(["irl", "fx:EUR"], (await Search("eur", ["irl"], core)).Items.Select(i => i.Key));
        Assert.Equal(["irl"], (await Search("loyer", ["irl"], core)).Items.Select(i => i.Key));
        Assert.Equal(["cpi", "irl", "fx:EUR"], (await Search(null, ["cpi"], core)).Items.Select(i => i.Key));
        Assert.Empty((await Search("zzz", ["nope"], core)).Items);
    }

    [Fact]
    public async Task List_skips_a_contribution_that_throws()
    {
        var view = await List(1, new FakeContribution("bad", [], throws: true), new FakeContribution("core", [Owned(Info("cpi"))]));
        Assert.Equal(["cpi"], view.Items.Select(i => i.Key));
        Assert.Equal(1, view.Total);
    }

    [Fact]
    public async Task Points_rebase_the_selection_and_keep_its_order()
    {
        var view = Value(await Points("irl,cpi", new FakeContribution("core", [MonthlyCpi, QuarterlyIrl])));

        Assert.Equal(["irl", "cpi"], view.Series.Select(s => s.Key));
        Assert.Equal(D(2025, 1, 1), view.BaseDate);
        var first = view.Series[1].Points[0];
        Assert.Equal(("100", "97.66"), (first.Value, first.Base100));
        Assert.Equal("100", view.Series[0].Points[0].Base100);
    }

    [Fact]
    public async Task Points_expose_the_items_behind_an_item_series()
    {
        var income = ItemId.New();
        var owned = Owned(Info($"income:{income.Value}", "income", "Bob salaire", item: income), (D(2025, 1, 1), 100m));

        var view = Value(await Points($"income:{income.Value},cpi", new FakeContribution("core", [MonthlyCpi, owned])));

        Assert.Equal([income], view.ExposedItems());
    }

    [Fact]
    public async Task Points_never_rebase_a_percent_series()
    {
        var yoy = Owned(Info("cpi:yoy", SeriesKinds.CpiYoy, unit: SeriesUnits.Percent), (D(2025, 1, 1), 2m));
        var view = Value(await Points("cpi:yoy", new FakeContribution("core", [yoy])));
        Assert.All(view.Series[0].Points, p => Assert.Null(p.Base100));
        Assert.Null(view.BaseDate);
    }

    [Theory]
    [InlineData("cpi,cpi")]
    [InlineData(null)]
    public async Task Points_refuse_an_invalid_list(string? s)
        => Assert.Equal(SeriesCodes.InvalidKey, FailureCode(await Points(s, new FakeContribution("core", [MonthlyCpi]))));

    [Fact]
    public async Task Points_answer_not_found_when_no_contribution_owns_a_key()
    {
        var result = await Points("cpi,nope", new FakeContribution("core", [MonthlyCpi]));
        Assert.Equal(SeriesCodes.NotFound, FailureCode(result));
        Assert.True(result.TryGetFailure(out var failure));
        Assert.Equal("nope", failure!.Metadata!["key"]);
    }

    private static string FailureCode(Result<SeriesPointsView> r)
    {
        Assert.True(r.TryGetFailure(out var failure));
        return failure!.Code;
    }
}
