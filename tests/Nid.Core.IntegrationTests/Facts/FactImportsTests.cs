using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Core.IntegrationTests.Facts;

/// <summary>2B1b: facts keep an import reference; a value can be superseded; the unguarded reads of the import and the export.</summary>
[Collection("postgres")]
public class FactImportsTests(PostgresFixture postgres)
{
    private static string Code<T>(Result<T> result) where T : notnull
    {
        Assert.True(result.TryGetFailure(out var failure), "expected a failure");
        return failure!.Code;
    }

    [Fact]
    public async Task A_value_recorded_with_an_import_mark_is_found_by_its_ref_and_listed_with_it()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice);
        var mark = new ImportMark("val-1", new string('a', 64), w.Alice);
        (await w.Valuations.RecordAsync(new NewValuation(item, new LocalDate(2025, 1, 31), FactWorld.Eur(100m), null, FactSource.Import, mark), TestContext.Current.CancellationToken)).Ok();

        var found = await w.FactImports.FindImportedAsync(FactTable.Valuation, ["val-1", "val-2"], TestContext.Current.CancellationToken);
        var row = Assert.Single(found["val-1"]);
        Assert.Equal((item, mark.Hash, w.Alice), (row.Item, row.Hash, row.ImportedBy));
        Assert.False(found.ContainsKey("val-2"));
        Assert.Equal(mark, Assert.Single(await w.FactImports.ValuationsOfAsync([item], TestContext.Current.CancellationToken)).Import);
    }

    [Fact]
    public async Task Superseding_a_value_retires_it_and_the_replacement_keeps_the_ref()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice);
        var mark = new ImportMark("val-1", new string('a', 64), w.Alice);
        var first = (await w.Valuations.RecordAsync(new NewValuation(item, new LocalDate(2025, 1, 31), FactWorld.Eur(100m), null, FactSource.Import, mark), TestContext.Current.CancellationToken)).Ok();

        var next = (await w.Valuations.SupersedeAsync(item, first.Id,
            new NewValuation(item, new LocalDate(2025, 2, 28), FactWorld.Eur(120m), null, FactSource.Import, mark with { Hash = new string('b', 64) }), TestContext.Current.CancellationToken)).Ok();

        var stored = Assert.Single(await w.FactImports.ValuationsOfAsync([item], TestContext.Current.CancellationToken));
        Assert.Equal((next.Id, new LocalDate(2025, 2, 28), new string('b', 64)), (stored.Valuation.Id, stored.Valuation.AsOf, stored.Import!.Hash));
        Assert.Equal(first.Id, (await w.Db.Valuations.AsNoTracking().SingleAsync(r => r.Id == next.Id, cancellationToken: TestContext.Current.CancellationToken)).SupersedesId);
    }

    [Fact]
    public async Task Superseding_onto_a_date_that_has_another_current_value_is_a_conflict_and_leaves_both()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice);
        var a = (await w.Valuations.RecordAsync(new NewValuation(item, new LocalDate(2025, 1, 31), FactWorld.Eur(100m), null), TestContext.Current.CancellationToken)).Ok();
        (await w.Valuations.RecordAsync(new NewValuation(item, new LocalDate(2025, 2, 28), FactWorld.Eur(110m), null), TestContext.Current.CancellationToken)).Ok();

        var moved = await w.Valuations.SupersedeAsync(item, a.Id, new NewValuation(item, new LocalDate(2025, 2, 28), FactWorld.Eur(1m), null), TestContext.Current.CancellationToken);

        Assert.Equal(CoreFactCodes.FactConflict, Code(moved));
        Assert.Equal(2, (await w.FactImports.ValuationsOfAsync([item], TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task An_import_never_retires_a_current_value_of_the_same_date_but_a_manual_write_does()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice);
        var date = new LocalDate(2025, 1, 31);
        var hand = (await w.Valuations.RecordAsync(new NewValuation(item, date, FactWorld.Eur(100m), null), TestContext.Current.CancellationToken)).Ok();

        var imported = await w.Valuations.RecordAsync(new NewValuation(item, date, FactWorld.Eur(120m), null, FactSource.Import, new ImportMark("val-1", new string('a', 64), w.Alice)), TestContext.Current.CancellationToken);

        Assert.Equal(CoreFactCodes.FactConflict, Code(imported));
        Assert.Null((await w.Db.Valuations.AsNoTracking().SingleAsync(r => r.Id == hand.Id, cancellationToken: TestContext.Current.CancellationToken)).RetiredAt);

        var manual = (await w.Valuations.RecordAsync(new NewValuation(item, date, FactWorld.Eur(130m), null), TestContext.Current.CancellationToken)).Ok();
        Assert.Equal(hand.Id, (await w.Db.Valuations.AsNoTracking().SingleAsync(r => r.Id == manual.Id, cancellationToken: TestContext.Current.CancellationToken)).SupersedesId);
    }

    [Fact]
    public async Task A_flow_carries_its_mark_and_a_superseded_flow_hands_the_ref_to_its_replacement()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice);
        var mark = new ImportMark("fl-1", new string('a', 64), w.Alice);
        var flow = (await w.Flows.RecordAsync(new NewFlow(item, new LocalDate(2025, 1, 15), FlowKind.Contribution, FactWorld.Eur(50m), null, null, null, FactSource.Import, mark), TestContext.Current.CancellationToken)).Ok();
        (await w.Flows.SupersedeAsync(item, flow.Id,
            new NewFlow(item, new LocalDate(2025, 1, 15), FlowKind.Contribution, FactWorld.Eur(60m), null, null, null, FactSource.Import, mark), TestContext.Current.CancellationToken)).Ok();

        var stored = Assert.Single(await w.FactImports.FlowsOfAsync([item], TestContext.Current.CancellationToken));
        Assert.Equal((60m, "fl-1"), (stored.Flow.Amount.Amount, stored.Import!.Ref));
        Assert.Single((await w.FactImports.FindImportedAsync(FactTable.Flow, ["fl-1"], TestContext.Current.CancellationToken))["fl-1"]);
    }

    [Fact]
    public async Task A_manual_fact_has_no_mark_and_the_schema_refuses_a_ref_without_a_hash()
    {
        await using var w = await FactWorld.NewAsync(postgres);
        var item = await w.ItemAsync(w.Alice);
        (await w.Valuations.RecordAsync(new NewValuation(item, new LocalDate(2025, 1, 31), FactWorld.Eur(100m), null), TestContext.Current.CancellationToken)).Ok();
        Assert.Null(Assert.Single(await w.FactImports.ValuationsOfAsync([item], TestContext.Current.CancellationToken)).Import);

        var broken = w.Db.Database.ExecuteSqlRawAsync("update core.valuation set import_ref = 'x'", cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => broken);
    }
}
