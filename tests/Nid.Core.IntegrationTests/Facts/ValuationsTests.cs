using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.IntegrationTests.Facts;

[Collection("postgres")]
public class ValuationsTests(PostgresFixture postgres)
{
    private static readonly LocalDate June = new(2025, 6, 1);

    private static Failure FailureOf<T>(Result<T> result) where T : notnull
    {
        Assert.True(result.TryGetFailure(out var failure), "expected a failure");
        return failure!;
    }

    private static Failure FailureOf(Result result)
    {
        Assert.True(result.TryGetFailure(out var failure), "expected a failure");
        return failure!;
    }

    private static string Code<T>(Result<T> result) where T : notnull => FailureOf(result).Code;

    private static string Code(Result result) => FailureOf(result).Code;

    private static NewValuation Value(ItemId item, LocalDate asOf, decimal amount) => new(item, asOf, FactWorld.Eur(amount), null);

    private static async Task<int> RowsAsync(FactWorld w) => await w.Db.Valuations.AsNoTracking().CountAsync();

    [Fact]
    public async Task Records_a_value_with_its_provenance()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice);

        var recorded = (await w.Valuations.RecordAsync(new NewValuation(item, June, FactWorld.Eur(-12.5m), "relevé"), default)).Ok();

        Assert.Equal((item, June, FactWorld.Eur(-12.5m), "relevé"), (recorded.Item, recorded.AsOf, recorded.Value, recorded.Note));
        var row = await w.Db.Valuations.AsNoTracking().SingleAsync();
        Assert.Equal(("manual", "ui", w.Alice.Value, "EUR", (Guid?)null, (Instant?)null),
            (row.Source, row.CreatedVia, row.CreatedByPersonId, row.Currency, row.SupersedesId, row.RetiredAt));
    }

    [Fact]
    public async Task Recording_the_same_date_again_supersedes_the_current_value()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice);
        var first = (await w.Valuations.RecordAsync(Value(item, June, 100m), default)).Ok();

        var second = (await w.Valuations.RecordAsync(Value(item, June, 110m), default)).Ok();

        var rows = await w.Db.Valuations.AsNoTracking().OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.NotNull(rows.Single(r => r.Id == first.Id).RetiredAt);
        Assert.Equal(first.Id, rows.Single(r => r.Id == second.Id).SupersedesId);
        Assert.Equal(FactWorld.Eur(110m), (await w.Valuations.LatestAsync(item, June, default)).Ok().Value!.Value);
    }

    [Fact]
    public async Task Latest_is_unknown_before_the_first_value_and_goes_back_after_a_void()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice);
        (await w.Valuations.RecordAsync(Value(item, new LocalDate(2025, 5, 31), 90m), default)).Ok();
        var june = (await w.Valuations.RecordAsync(Value(item, new LocalDate(2025, 6, 30), 100m), default)).Ok();

        Assert.Null((await w.Valuations.LatestAsync(item, new LocalDate(2025, 5, 30), default)).Ok().Value);
        Assert.Equal(june.Id, (await w.Valuations.LatestAsync(item, new LocalDate(2025, 6, 30), default)).Ok().Value!.Id);

        Assert.True((await w.Valuations.VoidAsync(item, june.Id, default)).IsSuccess);

        Assert.Equal(FactWorld.Eur(90m), (await w.Valuations.LatestAsync(item, new LocalDate(2025, 6, 30), default)).Ok().Value!.Value);
    }

    [Fact]
    public async Task Series_lists_current_values_of_the_range_by_date()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice);
        foreach (var (day, amount) in new[] { (new LocalDate(2025, 3, 31), 3m), (new LocalDate(2025, 1, 31), 1m), (new LocalDate(2025, 2, 28), 2m) })
            (await w.Valuations.RecordAsync(Value(item, day, amount), default)).Ok();
        var february = (await w.Valuations.LatestAsync(item, new LocalDate(2025, 2, 28), default)).Ok().Value!;
        Assert.True((await w.Valuations.VoidAsync(item, february.Id, default)).IsSuccess);

        var range = DateRange.Create(new LocalDate(2025, 1, 1), new LocalDate(2025, 3, 31)).Ok();
        var series = (await w.Valuations.SeriesAsync(item, range, default)).Ok();

        Assert.Equal([new LocalDate(2025, 1, 31)], series.Select(v => v.AsOf));
    }

    [Fact]
    public async Task An_item_the_actor_cannot_read_does_not_exist()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice, Visibility.Private);
        var recorded = (await w.Valuations.RecordAsync(Value(item, June, 1m), default)).Ok();

        w.Act(w.Bob);

        Assert.IsType<NotFoundFailure>(FailureOf(await w.Valuations.RecordAsync(Value(item, June, 2m), default)));
        Assert.IsType<NotFoundFailure>(FailureOf(await w.Valuations.VoidAsync(item, recorded.Id, default)));
        Assert.IsType<NotFoundFailure>(FailureOf(await w.Valuations.LatestAsync(item, June, default)));
        Assert.Equal(1, await RowsAsync(w));
    }

    [Fact]
    public async Task Read_access_alone_writes_nothing()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice, Visibility.Shared);

        w.Act(w.Bob);

        Assert.IsType<UnauthorizedFailure>(FailureOf(await w.Valuations.RecordAsync(Value(item, June, 2m), default)));
        Assert.Null((await w.Valuations.LatestAsync(item, June, default)).Ok().Value);
        Assert.Equal(0, await RowsAsync(w));
    }

    [Fact]
    public async Task An_actor_without_a_person_is_not_permitted()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice);

        w.ActAs(Actor.System);

        Assert.IsType<UnauthorizedFailure>(FailureOf(await w.Valuations.RecordAsync(Value(item, June, 2m), default)));
        Assert.Equal(0, await RowsAsync(w));
    }

    [Fact]
    public async Task Refuses_an_item_without_currency_and_another_currency()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var bare = await w.ItemAsync(w.Alice, currency: null);
        var euros = await w.ItemAsync(w.Alice);

        Assert.Equal(CoreFactCodes.ItemWithoutCurrency, Code(await w.Valuations.RecordAsync(Value(bare, June, 1m), default)));
        Assert.Equal(CoreFactCodes.CurrencyMismatch,
            Code(await w.Valuations.RecordAsync(new NewValuation(euros, June, FactWorld.Usd(1m), null), default)));
        Assert.Equal(0, await RowsAsync(w));
    }

    [Fact]
    public async Task The_household_today_is_accepted_and_tomorrow_refused()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice);

        Assert.True((await w.Valuations.RecordAsync(Value(item, FactWorld.Today, 1m), default)).IsSuccess);
        Assert.Equal(CoreFactCodes.DateInFuture, Code(await w.Valuations.RecordAsync(Value(item, FactWorld.Today.PlusDays(1), 1m), default)));
        Assert.Equal(1, await RowsAsync(w));
    }

    [Fact]
    public async Task An_evening_in_Paris_is_already_the_next_day()
    {
        // 23:30 UTC on 30 June is 01:30 on 1 July in Europe/Paris: the household's today is the 1st.
        await using var w = await FactWorld.NewAsync(postgres, Instant.FromUtc(2025, 6, 30, 23, 30));
        var item = await w.ItemAsync(w.Alice);
        var utcDay = new LocalDate(2025, 6, 30);

        Assert.True((await w.Valuations.RecordAsync(Value(item, utcDay.PlusDays(1), 1m), default)).IsSuccess);
        Assert.Equal(CoreFactCodes.DateInFuture, Code(await w.Valuations.RecordAsync(Value(item, utcDay.PlusDays(2), 1m), default)));
        Assert.True((await w.Flows.RecordAsync(
            new NewFlow(item, utcDay.PlusDays(1), FlowKind.Contribution, FactWorld.Eur(1m), null, null, null), default)).IsSuccess);
        Assert.Equal(1, await RowsAsync(w));
    }

    [Fact]
    public async Task An_amount_beyond_numeric_19_4_is_invalid_not_a_crash()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice);

        Assert.Equal(CoreFactCodes.InvalidFact, Code(await w.Valuations.RecordAsync(Value(item, June, 1e16m), default)));
        Assert.Equal(0, await RowsAsync(w));
    }

    [Fact]
    public async Task Void_answers_for_a_missing_a_foreign_and_a_retired_fact()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var mine = await w.ItemAsync(w.Alice);
        var other = await w.ItemAsync(w.Alice);
        var onOther = (await w.Valuations.RecordAsync(Value(other, June, 5m), default)).Ok();
        var onMine = (await w.Valuations.RecordAsync(Value(mine, June, 5m), default)).Ok();

        Assert.Equal(CoreFactCodes.FactNotFound, Code(await w.Valuations.VoidAsync(mine, Guid.NewGuid(), default)));
        Assert.Equal(CoreFactCodes.FactNotFound, Code(await w.Valuations.VoidAsync(mine, onOther.Id, default)));
        Assert.Null(await w.Db.Valuations.AsNoTracking().Where(r => r.Id == onOther.Id).Select(r => r.RetiredAt).SingleAsync());

        Assert.True((await w.Valuations.VoidAsync(mine, onMine.Id, default)).IsSuccess);
        Assert.Equal(CoreFactCodes.FactRetired, Code(await w.Valuations.VoidAsync(mine, onMine.Id, default)));
    }

    [Fact]
    public async Task Parallel_records_of_one_date_leave_one_current_value_and_no_exception()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice);
        var worlds = Enumerable.Range(0, 10).Select(_ => w.ForSameDatabase()).ToList();

        var results = await Task.WhenAll(worlds.Select((x, i) => x.Valuations.RecordAsync(Value(item, June, 100m + i), default)));

        foreach (var x in worlds)
            await x.DisposeAsync();
        Assert.All(results.Where(r => !r.IsSuccess), r => Assert.Equal(CoreFactCodes.FactConflict, Code(r)));
        Assert.Equal(1, await w.Db.Valuations.AsNoTracking().CountAsync(r => r.RetiredAt == null));
    }
}
