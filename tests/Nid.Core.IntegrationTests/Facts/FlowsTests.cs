using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.IntegrationTests.Facts;

[Collection("postgres")]
public class FlowsTests(PostgresFixture postgres)
{
    private static readonly LocalDate V = new(2025, 6, 15);

    private static NewFlow Flow(ItemId item, FlowKind kind, decimal amount, LocalDate? on = null, string? externalRef = null)
        => new(item, on ?? V, kind, FactWorld.Eur(amount), null, externalRef, null);

    private static DateRange UpTo(LocalDate v) => DateRange.Create(LocalDate.MinIsoValue, v.PlusDays(1)).Ok();

    private static string Code<T>(Result<T> result) where T : notnull
    {
        Assert.True(result.TryGetFailure(out var failure), "expected a failure");
        return failure!.Code;
    }

    private static string Code(Result result)
    {
        Assert.True(result.TryGetFailure(out var failure), "expected a failure");
        return failure!.Code;
    }

    private static readonly IReadOnlySet<FlowKind> Net = new HashSet<FlowKind> { FlowKind.Opening, FlowKind.Contribution };

    [Fact]
    public async Task Find_many_reads_active_flows_by_id_and_skips_voided_and_superseded_ones()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice);
        var active = (await w.Flows.RecordAsync(Flow(item, FlowKind.Income, 10m), TestContext.Current.CancellationToken)).Ok();
        var voided = (await w.Flows.RecordAsync(Flow(item, FlowKind.Income, 20m), TestContext.Current.CancellationToken)).Ok();
        var typo = (await w.Flows.RecordAsync(Flow(item, FlowKind.Income, 30m), TestContext.Current.CancellationToken)).Ok();
        Assert.True((await w.Flows.VoidAsync(item, voided.Id, TestContext.Current.CancellationToken)).IsSuccess);
        var fixedFlow = (await w.Flows.SupersedeAsync(item, typo.Id, Flow(item, FlowKind.Income, 31m), TestContext.Current.CancellationToken)).Ok();

        var found = await w.Flows.FindManyAsync([active.Id, voided.Id, typo.Id, fixedFlow.Id, Guid.NewGuid()], TestContext.Current.CancellationToken);

        Assert.Equal(new[] { active.Id, fixedFlow.Id }.Order(), found.Select(f => f.Id).Order());
        Assert.All(found, f => { Assert.Equal(item, f.Item); Assert.Equal(FlowKind.Income, f.Kind); });
        Assert.Empty(await w.Flows.FindManyAsync([], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Sum_counts_a_flow_on_v_up_to_v_and_not_up_to_the_day_before()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice);
        (await w.Flows.RecordAsync(Flow(item, FlowKind.Opening, 20_000m, new LocalDate(2024, 12, 31)), TestContext.Current.CancellationToken)).Ok();
        (await w.Flows.RecordAsync(Flow(item, FlowKind.Contribution, 2_000m, V), TestContext.Current.CancellationToken)).Ok();

        Assert.Equal(FactWorld.Eur(22_000m), (await w.Flows.SumAsync(item, Net, UpTo(V), TestContext.Current.CancellationToken)).Ok());
        Assert.Equal(FactWorld.Eur(20_000m), (await w.Flows.SumAsync(item, Net, UpTo(V.PlusDays(-1)), TestContext.Current.CancellationToken)).Ok());
    }

    [Fact]
    public async Task Sum_is_zero_in_the_item_currency_when_nothing_matches_and_ignores_retired_rows()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice);
        var contribution = (await w.Flows.RecordAsync(Flow(item, FlowKind.Contribution, 500m), TestContext.Current.CancellationToken)).Ok();
        Assert.True((await w.Flows.VoidAsync(item, contribution.Id, TestContext.Current.CancellationToken)).IsSuccess);

        Assert.Equal(FactWorld.Eur(0m), (await w.Flows.SumAsync(item, Net, UpTo(V), TestContext.Current.CancellationToken)).Ok());
    }

    [Fact]
    public async Task Supersede_retires_the_old_flow_and_records_the_replacement()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice);
        var typo = (await w.Flows.RecordAsync(Flow(item, FlowKind.Contribution, 200m), TestContext.Current.CancellationToken)).Ok();

        var fixedFlow = (await w.Flows.SupersedeAsync(item, typo.Id, Flow(item, FlowKind.Contribution, 2_000m), TestContext.Current.CancellationToken)).Ok();

        var rows = await w.Db.Flows.AsNoTracking().ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(rows.Single(r => r.Id == typo.Id).RetiredAt);
        Assert.Equal(typo.Id, rows.Single(r => r.Id == fixedFlow.Id).SupersedesId);
        Assert.Equal(CoreFactCodes.FactRetired, Code(await w.Flows.SupersedeAsync(item, typo.Id, Flow(item, FlowKind.Contribution, 1m), TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_replacement_on_another_item_is_invalid()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice);
        var other = await w.ItemAsync(w.Alice);
        var flow = (await w.Flows.RecordAsync(Flow(item, FlowKind.Contribution, 200m), TestContext.Current.CancellationToken)).Ok();

        Assert.Equal(CoreFactCodes.InvalidFact, Code(await w.Flows.SupersedeAsync(item, flow.Id, Flow(other, FlowKind.Contribution, 1m), TestContext.Current.CancellationToken)));
        Assert.Null(await w.Db.Flows.AsNoTracking().Where(r => r.Id == flow.Id).Select(r => r.RetiredAt).SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task List_orders_flows_by_date_and_filters_kinds()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice);
        var first = (await w.Flows.RecordAsync(Flow(item, FlowKind.Contribution, 1m), TestContext.Current.CancellationToken)).Ok();
        var second = (await w.Flows.RecordAsync(Flow(item, FlowKind.Expense, 2m), TestContext.Current.CancellationToken)).Ok();
        var earlier = (await w.Flows.RecordAsync(Flow(item, FlowKind.Withdrawal, 3m, V.PlusDays(-1)), TestContext.Current.CancellationToken)).Ok();

        var all = (await w.Flows.ListAsync(item, UpTo(V), null, TestContext.Current.CancellationToken)).Ok();
        var expenses = (await w.Flows.ListAsync(item, UpTo(V), new HashSet<FlowKind> { FlowKind.Expense }, TestContext.Current.CancellationToken)).Ok();

        // The clock is fixed, so first and second share created_at and their order falls to the id: only the date
        // order is asserted, the tie is not.
        Assert.Equal(earlier.Id, all[0].Id);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), all.Skip(1).Select(f => f.Id).Order());
        Assert.Equal([second.Id], expenses.Select(f => f.Id));
    }

    [Fact]
    public async Task One_current_opening_and_one_current_external_reference()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice);
        var opening = (await w.Flows.RecordAsync(Flow(item, FlowKind.Opening, 1_000m), TestContext.Current.CancellationToken)).Ok();
        (await w.Flows.RecordAsync(Flow(item, FlowKind.Contribution, 10m, externalRef: "transfer:1"), TestContext.Current.CancellationToken)).Ok();

        Assert.Equal(CoreFactCodes.OpeningExists, Code(await w.Flows.RecordAsync(Flow(item, FlowKind.Opening, 2_000m), TestContext.Current.CancellationToken)));
        Assert.Equal(CoreFactCodes.ExternalRefTaken,
            Code(await w.Flows.RecordAsync(Flow(item, FlowKind.Contribution, 20m, externalRef: "transfer:1"), TestContext.Current.CancellationToken)));

        // Superseding the opening itself is a correction, not a second opening.
        (await w.Flows.SupersedeAsync(item, opening.Id, Flow(item, FlowKind.Opening, 1_200m), TestContext.Current.CancellationToken)).Ok();
        // The same reference on another item is another transfer's leg.
        var other = await w.ItemAsync(w.Alice);
        (await w.Flows.RecordAsync(Flow(other, FlowKind.Withdrawal, 10m, externalRef: "transfer:1"), TestContext.Current.CancellationToken)).Ok();
    }

    [Fact]
    public async Task A_flow_is_positive_and_not_in_the_future()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice);

        Assert.Equal(CoreFactCodes.AmountNotPositive, Code(await w.Flows.RecordAsync(Flow(item, FlowKind.Contribution, 0m), TestContext.Current.CancellationToken)));
        Assert.Equal(CoreFactCodes.DateInFuture,
            Code(await w.Flows.RecordAsync(Flow(item, FlowKind.Contribution, 1m, FactWorld.Today.PlusDays(1)), TestContext.Current.CancellationToken)));
        Assert.Equal(0, await w.Db.Flows.AsNoTracking().CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Void_of_a_flow_of_another_item_touches_nothing()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice);
        var other = await w.ItemAsync(w.Alice);
        var onOther = (await w.Flows.RecordAsync(Flow(other, FlowKind.Contribution, 1m), TestContext.Current.CancellationToken)).Ok();

        Assert.Equal(CoreFactCodes.FactNotFound, Code(await w.Flows.VoidAsync(item, onOther.Id, TestContext.Current.CancellationToken)));
        Assert.Equal(CoreFactCodes.FactNotFound, Code(await w.Flows.SupersedeAsync(item, onOther.Id, Flow(item, FlowKind.Contribution, 1m), TestContext.Current.CancellationToken)));
        Assert.Null(await w.Db.Flows.AsNoTracking().Where(r => r.Id == onOther.Id).Select(r => r.RetiredAt).SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Another_adult_cannot_read_a_private_item_s_flows()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice, Visibility.Private);
        (await w.Flows.RecordAsync(Flow(item, FlowKind.Contribution, 1m), TestContext.Current.CancellationToken)).Ok();

        w.Act(w.Bob);

        Assert.True((await w.Flows.SumAsync(item, Net, UpTo(V), TestContext.Current.CancellationToken)).TryGetFailure(out var failure));
        Assert.IsType<NotFoundFailure>(failure);
        Assert.True((await w.Flows.ListAsync(item, UpTo(V), null, TestContext.Current.CancellationToken)).TryGetFailure(out failure));
        Assert.IsType<NotFoundFailure>(failure);
    }

    [Fact]
    public async Task Parallel_openings_leave_one_current_and_no_exception()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice);
        var worlds = Enumerable.Range(0, 10).Select(_ => w.ForSameDatabase()).ToList();

        var results = await Task.WhenAll(worlds.Select((x, i) => x.Flows.RecordAsync(Flow(item, FlowKind.Opening, 100m + i), default)));

        foreach (var x in worlds)
            await x.DisposeAsync();
        Assert.All(results.Where(r => !r.IsSuccess), r => Assert.Contains(Code(r), new[] { CoreFactCodes.OpeningExists, CoreFactCodes.FactConflict }));
        Assert.Equal(1, await w.Db.Flows.AsNoTracking().CountAsync(r => r.Kind == "opening" && r.RetiredAt == null, cancellationToken: TestContext.Current.CancellationToken));
    }
}
