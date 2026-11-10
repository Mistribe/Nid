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

    public async Task<IReadOnlyList<Flow>> CounterpartsAsync(ItemId item, CancellationToken ct)
    {
        var id = item.Value;
        var references = db.Flows.Where(r => r.ItemId == id && r.RetiredAt == null && r.ExternalRef != null && r.ExternalRef.StartsWith("transfer:")).Select(r => r.ExternalRef);
        return (await db.Flows.AsNoTracking().Where(r => r.ItemId != id && r.RetiredAt == null && references.Contains(r.ExternalRef)).ToListAsync(ct))
            .Select(ToFlow).ToList();
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

        var row = new FlowRow
        {
            Id = Guid.CreateVersion7(),
            ItemId = item,
            OccurredOn = flow.OccurredOn,
            Kind = kind,
            Amount = flow.Amount.Amount,
            Currency = w!.Currency.Code,
            Category = flow.Category,
            ExternalRef = flow.ExternalRef,
            Note = flow.Note,
            Source = flow.Source == FactSource.Import ? "import" : "manual",
            CreatedVia = w.Via,
            CreatedByPersonId = w.Person.Value,
            CreatedAt = now,
            SupersedesId = old?.Id,
            ImportRef = flow.Import?.Ref,
            ImportHash = flow.Import?.Hash,
            ImportedBy = flow.Import?.ImportedBy.Value,
            DocumentId = old?.DocumentId, // a correction keeps its source
        };
        db.Flows.Add(row);
        await db.SaveChangesAsync(ct);
        return Result.Success(ToFlow(row));
    }

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
