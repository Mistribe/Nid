using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.IntegrationTests.Facts;

/// <summary>Batched reads spec §2 and §4: the batched fact reads give, item by item, what the single reads give.</summary>
[Collection("postgres")]
public class BatchedFactsTests(PostgresFixture postgres)
{
    private static readonly LocalDate June = new(2025, 6, 1);
    private static readonly DateRange Everything = DateRange.Create(LocalDate.MinIsoValue, null).Ok();
    private static readonly DateRange Window = DateRange.Create(June.PlusDays(1), June.PlusDays(4)).Ok(); // [2 June, 5 June)

    private static NewValuation Value(ItemId item, int day, decimal amount) => new(item, June.PlusDays(day - 1), FactWorld.Eur(amount), null);

    private static NewFlow Flow(ItemId item, FlowKind kind, int day, decimal amount)
        => new(item, June.PlusDays(day - 1), kind, FactWorld.Eur(amount), null, null, null);

    private static Failure FailureOf<T>(Result<T> result) where T : notnull
    {
        Assert.True(result.TryGetFailure(out var failure), "expected a failure");
        return failure!;
    }

    [Fact]
    public async Task Each_item_reads_what_its_single_read_gives_in_the_same_order()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var mine = await w.ItemAsync(w.Alice);
        var bobs = await w.ItemAsync(w.Bob, Visibility.Shared); // Alice reads it, Bob writes it
        var closed = await w.ItemAsync(w.Alice);
        var empty = await w.ItemAsync(w.Alice);
        // Interleaved across items by date and by creation; three flows on 3 June, two of them on the same item.
        (await w.Valuations.RecordAsync(Value(mine, 10, 100m), default)).Ok();
        (await w.Flows.RecordAsync(Flow(mine, FlowKind.Contribution, 3, 10m), default)).Ok();
        (await w.Valuations.RecordAsync(Value(closed, 4, 50m), default)).Ok();
        (await w.Flows.RecordAsync(Flow(closed, FlowKind.Opening, 1, 50m), default)).Ok();
        (await w.Valuations.RecordAsync(Value(mine, 1, 90m), default)).Ok();
        (await w.Flows.RecordAsync(Flow(mine, FlowKind.Withdrawal, 2, 5m), default)).Ok();
        (await w.Flows.RecordAsync(Flow(mine, FlowKind.Expense, 3, 1m), default)).Ok();
        w.Act(w.Bob);
        (await w.Valuations.RecordAsync(Value(bobs, 5, 70m), default)).Ok();
        (await w.Flows.RecordAsync(Flow(bobs, FlowKind.Contribution, 3, 7m), default)).Ok();
        (await w.Valuations.RecordAsync(Value(bobs, 2, 60m), default)).Ok();
        w.Act(w.Alice);
        Assert.True((await w.Items.ArchiveAsync(closed, FactWorld.Now, default)).IsSuccess);
        ItemId[] items = [mine, bobs, closed, empty];
        var kinds = new HashSet<FlowKind> { FlowKind.Contribution, FlowKind.Expense };

        foreach (var range in new[] { Everything, Window })
        {
            var series = (await w.Valuations.SeriesManyAsync(items, range, default)).Ok();
            var all = (await w.Flows.ListManyAsync(items, range, null, default)).Ok();
            var some = (await w.Flows.ListManyAsync(items, range, kinds, default)).Ok();

            Assert.Equal(items.OrderBy(i => i.Value), series.Keys.OrderBy(i => i.Value));
            foreach (var item in items)
            {
                Assert.Equal((await w.Valuations.SeriesAsync(item, range, default)).Ok(), series[item]);
                Assert.Equal((await w.Flows.ListAsync(item, range, null, default)).Ok(), all[item]);
                Assert.Equal((await w.Flows.ListAsync(item, range, kinds, default)).Ok(), some[item]);
            }
            Assert.Empty(series[empty]);
            Assert.Empty(all[empty]);
        }

        var everything = (await w.Valuations.SeriesManyAsync(items, Everything, default)).Ok();
        Assert.Equal([90m, 100m], everything[mine].Select(v => v.Value.Amount));
        Assert.Single(everything[closed]); // archived: its facts are read
    }

    [Fact]
    public async Task One_item_that_cannot_be_read_refuses_the_whole_block_as_it_refuses_alone()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var mine = await w.ItemAsync(w.Alice);
        var hidden = await w.ItemAsync(w.Bob); // private to Bob
        var bare = await w.ItemAsync(w.Alice, currency: null);
        (await w.Valuations.RecordAsync(Value(mine, 1, 100m), default)).Ok();

        foreach (var refused in new[] { hidden, ItemId.New(), bare })
        {
            var alone = FailureOf(await w.Valuations.SeriesAsync(refused, Everything, default));
            var series = FailureOf(await w.Valuations.SeriesManyAsync([mine, refused], Everything, default));
            var flows = FailureOf(await w.Flows.ListManyAsync([mine, refused], Everything, null, default));
            Assert.Equal((alone.GetType(), alone.Code), (series.GetType(), series.Code));
            Assert.Equal((alone.GetType(), alone.Code), (flows.GetType(), flows.Code));
        }
        Assert.IsType<NotFoundFailure>(FailureOf(await w.Valuations.SeriesManyAsync([mine, hidden], Everything, default)));

        w.ActAs(Actor.System);
        Assert.IsType<UnauthorizedFailure>(FailureOf(await w.Valuations.SeriesManyAsync([mine], Everything, default)));
        Assert.IsType<UnauthorizedFailure>(FailureOf(await w.Flows.ListManyAsync([mine], Everything, null, default)));
    }

    [Fact]
    public async Task No_item_reads_an_empty_block_and_a_repeated_item_reads_once()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var mine = await w.ItemAsync(w.Alice);
        (await w.Valuations.RecordAsync(Value(mine, 1, 100m), default)).Ok();
        (await w.Flows.RecordAsync(Flow(mine, FlowKind.Contribution, 1, 10m), default)).Ok();

        Assert.Empty((await w.Valuations.SeriesManyAsync([], Everything, default)).Ok());
        Assert.Empty((await w.Flows.ListManyAsync([], Everything, null, default)).Ok());
        var series = (await w.Valuations.SeriesManyAsync([mine, mine], Everything, default)).Ok();
        var flows = (await w.Flows.ListManyAsync([mine, mine], Everything, null, default)).Ok();
        Assert.Equal((await w.Valuations.SeriesAsync(mine, Everything, default)).Ok(), Assert.Single(series).Value);
        Assert.Equal((await w.Flows.ListAsync(mine, Everything, null, default)).Ok(), Assert.Single(flows).Value);
    }
}
