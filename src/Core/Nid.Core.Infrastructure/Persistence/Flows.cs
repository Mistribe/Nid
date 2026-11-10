using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Core.Domain;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Infrastructure.Persistence;

/// <summary>2A spec §4 and §5; <c>core-data-model.md</c> §5.3 and §5.4.</summary>
public sealed class Flows(CoreDbContext db, FactGate gate, IClock clock) : IFlows
{
    public Task<Result<Flow>> RecordAsync(NewFlow flow, CancellationToken ct)
        => gate.WriteAsync(() => WriteAsync(flow, replacing: null, ct), ct);

    public Task<Result<Flow>> SupersedeAsync(ItemId item, Guid id, NewFlow replacement, CancellationToken ct) => gate.WriteAsync(async () =>
    {
        if (replacement.Item != item)
            return Result.Failure<Flow>(new Failure(CoreFactCodes.InvalidFact, "A replacement stays on the same item.",
                new Dictionary<string, object?> { ["field"] = "item" }));
        return await WriteAsync(replacement, replacing: id, ct);
    }, ct);

    public async Task<Result> VoidAsync(ItemId item, Guid id, CancellationToken ct)
    {
        var result = await gate.WriteAsync(async () =>
        {
            var writer = await gate.ForWriteAsync(item, ct);
            if (writer.TryGetFailure(out var denied))
                return Result.Failure<bool>(denied!);
            var row = await FindCurrentAsync(item, id, ct);
            if (!row.TryGetValue(out var current))
                return Result.Failure<bool>(FailureOf(row));

            current!.RetiredAt = clock.GetCurrentInstant();
            await db.SaveChangesAsync(ct);
            return Result.Success(true);
        }, ct);
        return result.TryGetFailure(out var failure) ? Result.Failure(failure!) : Result.Success();
    }

    public async Task<Result<IReadOnlyList<Flow>>> ListAsync(ItemId item, DateRange range, IReadOnlySet<FlowKind>? kinds, CancellationToken ct)
    {
        var read = await gate.ForReadAsync(item, ct);
        if (read.TryGetFailure(out var denied))
            return Result.Failure<IReadOnlyList<Flow>>(denied!);

        var rows = await Current(item, range, kinds)
            .OrderBy(r => r.OccurredOn).ThenBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .ToListAsync(ct);
        return Result.Success<IReadOnlyList<Flow>>(rows.Select(ToFlow).ToList());
    }

    public async Task<Result<IReadOnlyDictionary<ItemId, IReadOnlyList<Flow>>>> ListManyAsync(
        IReadOnlyCollection<ItemId> items, DateRange range, IReadOnlySet<FlowKind>? kinds, CancellationToken ct)
    {
        var read = await gate.ForReadManyAsync(items, ct);
        if (read.TryGetFailure(out var denied))
            return Result.Failure<IReadOnlyDictionary<ItemId, IReadOnlyList<Flow>>>(denied!);
        if (items.Count == 0)
            return Result.Success<IReadOnlyDictionary<ItemId, IReadOnlyList<Flow>>>(new Dictionary<ItemId, IReadOnlyList<Flow>>());

        var ids = items.Select(i => i.Value).Distinct().ToList();
        // ListAsync's filter and order; a lookup keeps each item's rows in that order.
        var rows = (await Within(db.Flows.AsNoTracking().Where(r => ids.Contains(r.ItemId)), range, kinds)
            .OrderBy(r => r.OccurredOn).ThenBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .ToListAsync(ct)).ToLookup(r => r.ItemId);
        return Result.Success<IReadOnlyDictionary<ItemId, IReadOnlyList<Flow>>>(
            ids.ToDictionary(id => new ItemId(id), id => (IReadOnlyList<Flow>)rows[id].Select(ToFlow).ToList()));
    }

    public async Task<IReadOnlyList<Flow>> FindManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        return (await db.Flows.AsNoTracking().Where(r => r.RetiredAt == null && wanted.Contains(r.Id)).ToListAsync(ct)).Select(ToFlow).ToList();
    }

    public async Task<Result<Money>> SumAsync(ItemId item, IReadOnlySet<FlowKind> kinds, DateRange range, CancellationToken ct)
    {
        var read = await gate.ForReadAsync(item, ct);
        if (!read.TryGetValue(out var currency))
            return Result.Failure<Money>(FailureOf(read));

        var sum = await Current(item, range, kinds).SumAsync(r => r.Amount, ct);
        return Money.Create(sum, currency);
    }

    private async Task<Result<Flow>> WriteAsync(NewFlow flow, Guid? replacing, CancellationToken ct)
    {
        var writer = await gate.ForWriteAsync(flow.Item, ct);
        if (!writer.TryGetValue(out var w))
            return Result.Failure<Flow>(FailureOf(writer));
        var checks = await gate.CheckAsync(w!, flow.Amount, flow.OccurredOn, ct);
        if (checks.TryGetFailure(out var failure) || FactRules.CheckFlow(flow).TryGetFailure(out failure))
            return Result.Failure<Flow>(failure!);

        FlowRow? old = null;
        if (replacing is { } id)
        {
            var found = await FindCurrentAsync(flow.Item, id, ct);
            if (!found.TryGetValue(out old))
                return Result.Failure<Flow>(FailureOf(found));
        }

        var kind = Wire(flow.Kind);
        var item = flow.Item.Value;
        if (kind == "opening" && await db.Flows.AnyAsync(r => r.ItemId == item && r.Kind == "opening" && r.RetiredAt == null && r.Id != replacing, ct))
            return Result.Failure<Flow>(new Failure(CoreFactCodes.OpeningExists, "This item already has an opening; correct it instead.", null));
        if (flow.ExternalRef is { } reference
            && await db.Flows.AnyAsync(r => r.ItemId == item && r.ExternalRef == reference && r.RetiredAt == null && r.Id != replacing, ct))
            return Result.Failure<Flow>(new Failure(CoreFactCodes.ExternalRefTaken, "This item already has a flow with that reference.", null));

        var now = clock.GetCurrentInstant();
        if (old is not null)
        {
            old.RetiredAt = now;
            await db.SaveChangesAsync(ct); // retired first: the replacement may take its opening or its reference
        }

        var row = Row(flow, w!, now, old);
        db.Flows.Add(row);
        await db.SaveChangesAsync(ct);
        return Result.Success(ToFlow(row));
    }

    public Task<Result<IReadOnlyList<Flow>>> RecordManyAsync(ItemId item, IReadOnlyList<NewFlow> flows, CancellationToken ct) => gate.WriteAsync(async () =>
    {
        if (flows.Count == 0)
            return Result.Success<IReadOnlyList<Flow>>([]);
        var writer = await gate.ForWriteAsync(item, ct);
        if (!writer.TryGetValue(out var w))
            return Result.Failure<IReadOnlyList<Flow>>(FailureOf(writer));
        var today = await gate.TodayAsync(ct);

        // What RecordAsync asks the database once per flow, asked once for all: the references taken, an opening.
        var id = item.Value;
        var references = flows.Select(f => f.ExternalRef).OfType<string>().Distinct().ToList();
        var taken = references.Count == 0 ? [] : (await db.Flows.AsNoTracking()
            .Where(r => r.ItemId == id && r.RetiredAt == null && r.ExternalRef != null && references.Contains(r.ExternalRef))
            .Select(r => r.ExternalRef!).ToListAsync(ct)).ToHashSet();
        var opening = flows.Any(f => f.Kind == FlowKind.Opening)
            && await db.Flows.AnyAsync(r => r.ItemId == id && r.Kind == "opening" && r.RetiredAt == null, ct);

        var now = clock.GetCurrentInstant();
        var rows = new List<FlowRow>(flows.Count);
        for (var i = 0; i < flows.Count; i++)
        {
            var flow = flows[i];
            Failure? refused = null;
            if (flow.Item != item)
                refused = new Failure(CoreFactCodes.InvalidFact, "Every flow of the write is on its item.", new Dictionary<string, object?> { ["field"] = "item" });
            else if (FactGate.Check(w!, flow.Amount, flow.OccurredOn, today).TryGetFailure(out var failure) || FactRules.CheckFlow(flow).TryGetFailure(out failure))
                refused = failure;
            // Within the write as against the database: the second opening, the second use of a reference, is the one refused.
            else if (flow.Kind == FlowKind.Opening && opening)
                refused = new Failure(CoreFactCodes.OpeningExists, "This item already has an opening; correct it instead.", null);
            else if (flow.ExternalRef is { } reference && !taken.Add(reference))
                refused = new Failure(CoreFactCodes.ExternalRefTaken, "This item already has a flow with that reference.", null);
            if (refused is not null)
                return Result.Failure<IReadOnlyList<Flow>>(At(refused, i));
            opening |= flow.Kind == FlowKind.Opening;
            // One microsecond apart (the column's precision): the flows of a day keep the order they were given in, as one by one.
            rows.Add(Row(flow, w!, now.Plus(NodaTime.Duration.FromTicks(10L * i)), old: null));
        }
        db.Flows.AddRange(rows);
        await db.SaveChangesAsync(ct);
        return Result.Success<IReadOnlyList<Flow>>(rows.Select(ToFlow).ToList());
    }, ct);

    /// <summary>A refusal of <see cref="RecordManyAsync"/>, with the position of the flow it names; a typed failure stays as it is.</summary>
    private static Failure At(Failure failure, int index)
    {
        if (failure.GetType() != typeof(Failure))
            return failure;
        var metadata = failure.Metadata is { } m ? new Dictionary<string, object?>(m) : [];
        metadata[IFlows.RecordManyIndex] = index;
        return new Failure(failure.Code, failure.Message, metadata);
    }

    private static FlowRow Row(NewFlow flow, FactWriter w, Instant at, FlowRow? old) => new()
    {
        Id = Guid.CreateVersion7(),
        ItemId = flow.Item.Value,
        OccurredOn = flow.OccurredOn,
        Kind = Wire(flow.Kind),
        Amount = flow.Amount.Amount,
        Currency = w.Currency.Code,
        Category = flow.Category,
        ExternalRef = flow.ExternalRef,
        Note = flow.Note,
        Source = flow.Source == FactSource.Import ? "import" : "manual",
        CreatedVia = w.Via,
        CreatedByPersonId = w.Person.Value,
        CreatedAt = at,
        SupersedesId = old?.Id,
        ImportRef = flow.Import?.Ref,
        ImportHash = flow.Import?.Hash,
        ImportedBy = flow.Import?.ImportedBy.Value,
        DocumentId = old?.DocumentId, // a correction keeps its source
    };

    private async Task<Result<FlowRow>> FindCurrentAsync(ItemId item, Guid id, CancellationToken ct)
    {
        var row = await db.Flows.SingleOrDefaultAsync(r => r.Id == id && r.ItemId == item.Value, ct);
        if (row is null)
            return Result.Failure<FlowRow>(new Failure(CoreFactCodes.FactNotFound, "This item has no such flow.", null));
        if (row.RetiredAt is not null)
            return Result.Failure<FlowRow>(new Failure(CoreFactCodes.FactRetired, "This flow was already corrected or voided.", null));
        return Result.Success(row);
    }

    private IQueryable<FlowRow> Current(ItemId item, DateRange range, IReadOnlySet<FlowKind>? kinds)
        => Within(db.Flows.AsNoTracking().Where(r => r.ItemId == item.Value), range, kinds);

    /// <summary>The current rows of <paramref name="query"/> in <paramref name="range"/>, of <paramref name="kinds"/> (all when null).</summary>
    private static IQueryable<FlowRow> Within(IQueryable<FlowRow> query, DateRange range, IReadOnlySet<FlowKind>? kinds)
    {
        query = query.Where(r => r.RetiredAt == null && r.OccurredOn >= range.Start);
        if (range.EndBefore is { } end)
            query = query.Where(r => r.OccurredOn < end);
        if (kinds is not null)
        {
            var wire = kinds.Select(Wire).ToList();
            query = query.Where(r => wire.Contains(r.Kind));
        }

        return query;
    }

    private static string Wire(FlowKind kind) => kind switch
    {
        FlowKind.Contribution => "contribution",
        FlowKind.Withdrawal => "withdrawal",
        FlowKind.Opening => "opening",
        FlowKind.Income => "income",
        FlowKind.Expense => "expense",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static FlowKind Kind(string wire) => wire switch
    {
        "contribution" => FlowKind.Contribution,
        "withdrawal" => FlowKind.Withdrawal,
        "opening" => FlowKind.Opening,
        "income" => FlowKind.Income,
        "expense" => FlowKind.Expense,
        _ => throw new InvalidOperationException($"Unknown stored flow kind '{wire}'."),
    };

    internal static Flow ToFlow(FlowRow row)
    {
        if (!Currency.TryCreate(row.Currency.Trim(), out var currency) || !Money.Create(row.Amount, currency).TryGetValue(out var amount))
            throw new InvalidOperationException("A stored flow has an invalid amount or currency.");
        return new Flow(row.Id, new ItemId(row.ItemId), row.OccurredOn, Kind(row.Kind), amount, row.Category, row.ExternalRef, row.Note, row.DocumentId);
    }

    private static Failure FailureOf<T>(Result<T> result) where T : notnull
        => result.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException("Not a failure.");
}
