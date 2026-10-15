using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Core.Domain;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Infrastructure.Persistence;

/// <summary>2A spec §4 and §5; <c>core-data-model.md</c> §5.2 and §5.4.</summary>
public sealed class Valuations(CoreDbContext db, FactGate gate, IClock clock) : IValuations
{
    public Task<Result<Valuation>> RecordAsync(NewValuation valuation, CancellationToken ct) => gate.WriteAsync(async () =>
    {
        var writer = await gate.ForWriteAsync(valuation.Item, ct);
        if (!writer.TryGetValue(out var w))
            return Result.Failure<Valuation>(FailureOf(writer));
        var checks = await gate.CheckAsync(w!, valuation.Value, valuation.AsOf, ct);
        if (checks.TryGetFailure(out var failure) || FactRules.CheckValuation(valuation).TryGetFailure(out failure))
            return Result.Failure<Valuation>(failure!);

        var now = clock.GetCurrentInstant();
        var current = await db.Valuations.SingleOrDefaultAsync(
            r => r.ItemId == valuation.Item.Value && r.AsOf == valuation.AsOf && r.RetiredAt == null, ct);
        if (current is not null && valuation.Import is not null)
            return Result.Failure<Valuation>(new Failure(CoreFactCodes.FactConflict, "This item already has a value on that date; an import never replaces it.", null));
        if (current is not null)
        {
            current.RetiredAt = now;
            await db.SaveChangesAsync(ct); // retired first: the insert below would break ux on (item_id, as_of)
        }

        var row = NewRow(valuation, w!, now, current?.Id);
        db.Valuations.Add(row);
        await db.SaveChangesAsync(ct);
        return Result.Success(ToValuation(row));
    }, ct);

    public Task<Result<Valuation>> SupersedeAsync(ItemId item, Guid id, NewValuation replacement, CancellationToken ct) => gate.WriteAsync(async () =>
    {
        if (replacement.Item != item)
            return Result.Failure<Valuation>(new Failure(CoreFactCodes.InvalidFact, "A correction stays on its item.", null));
        var writer = await gate.ForWriteAsync(item, ct);
        if (!writer.TryGetValue(out var w))
            return Result.Failure<Valuation>(FailureOf(writer));
        var checks = await gate.CheckAsync(w!, replacement.Value, replacement.AsOf, ct);
        if (checks.TryGetFailure(out var failure) || FactRules.CheckValuation(replacement).TryGetFailure(out failure))
            return Result.Failure<Valuation>(failure!);

        var old = await db.Valuations.SingleOrDefaultAsync(r => r.Id == id && r.ItemId == item.Value, ct);
        if (old is null)
            return Result.Failure<Valuation>(new Failure(CoreFactCodes.FactNotFound, "This item has no such value.", null));
        if (old.RetiredAt is not null)
            return Result.Failure<Valuation>(new Failure(CoreFactCodes.FactRetired, "This value was already corrected or voided.", null));

        var now = clock.GetCurrentInstant();
        old.RetiredAt = now;
        await db.SaveChangesAsync(ct); // retired first: the replacement may keep its date or its import ref
        var row = NewRow(replacement, w!, now, old.Id);
        db.Valuations.Add(row);
        await db.SaveChangesAsync(ct); // another current value on the new date breaks ux_valuation_current: FactConflict
        return Result.Success(ToValuation(row));
    }, ct);

    private static ValuationRow NewRow(NewValuation v, FactWriter w, Instant now, Guid? supersedes) => new()
    {
        Id = Guid.CreateVersion7(),
        ItemId = v.Item.Value,
        AsOf = v.AsOf,
        Amount = v.Value.Amount,
        Currency = w.Currency.Code,
        Note = v.Note,
        Source = v.Source == FactSource.Import ? "import" : "manual",
        CreatedVia = w.Via,
        CreatedByPersonId = w.Person.Value,
        CreatedAt = now,
        SupersedesId = supersedes,
        ImportRef = v.Import?.Ref,
        ImportHash = v.Import?.Hash,
        ImportedBy = v.Import?.ImportedBy.Value,
    };

    public async Task<Result> VoidAsync(ItemId item, Guid id, CancellationToken ct)
    {
        var result = await gate.WriteAsync(async () =>
        {
            var writer = await gate.ForWriteAsync(item, ct);
            if (writer.TryGetFailure(out var denied))
                return Result.Failure<bool>(denied!);

            var row = await db.Valuations.SingleOrDefaultAsync(r => r.Id == id && r.ItemId == item.Value, ct);
            if (row is null)
                return Result.Failure<bool>(new Failure(CoreFactCodes.FactNotFound, "This item has no such value.", null));
            if (row.RetiredAt is not null)
                return Result.Failure<bool>(new Failure(CoreFactCodes.FactRetired, "This value was already corrected or voided.", null));

            row.RetiredAt = clock.GetCurrentInstant();
            await db.SaveChangesAsync(ct);
            return Result.Success(true);
        }, ct);
        return result.TryGetFailure(out var failure) ? Result.Failure(failure!) : Result.Success();
    }

    public async Task<Result<LatestValuation>> LatestAsync(ItemId item, LocalDate on, CancellationToken ct)
    {
        var read = await gate.ForReadAsync(item, ct);
        if (read.TryGetFailure(out var denied))
            return Result.Failure<LatestValuation>(denied!);

        var row = await db.Valuations.AsNoTracking()
            .Where(r => r.ItemId == item.Value && r.RetiredAt == null && r.AsOf <= on)
            .OrderByDescending(r => r.AsOf)
            .FirstOrDefaultAsync(ct);
        return Result.Success(new LatestValuation(row is null ? null : ToValuation(row)));
    }

    public async Task<Result<IReadOnlyList<Valuation>>> SeriesAsync(ItemId item, DateRange range, CancellationToken ct)
    {
        var read = await gate.ForReadAsync(item, ct);
        if (read.TryGetFailure(out var denied))
            return Result.Failure<IReadOnlyList<Valuation>>(denied!);

        var end = range.EndBefore;
        var rows = await db.Valuations.AsNoTracking()
            .Where(r => r.ItemId == item.Value && r.RetiredAt == null && r.AsOf >= range.Start && (end == null || r.AsOf < end))
            .OrderBy(r => r.AsOf)
            .ToListAsync(ct);
        return Result.Success<IReadOnlyList<Valuation>>(rows.Select(ToValuation).ToList());
    }

    public async Task<Result<IReadOnlyDictionary<ItemId, IReadOnlyList<Valuation>>>> SeriesManyAsync(
        IReadOnlyCollection<ItemId> items, DateRange range, CancellationToken ct)
    {
        var read = await gate.ForReadManyAsync(items, ct);
        if (read.TryGetFailure(out var denied))
            return Result.Failure<IReadOnlyDictionary<ItemId, IReadOnlyList<Valuation>>>(denied!);
        if (items.Count == 0)
            return Result.Success<IReadOnlyDictionary<ItemId, IReadOnlyList<Valuation>>>(new Dictionary<ItemId, IReadOnlyList<Valuation>>());

        var ids = items.Select(i => i.Value).Distinct().ToList();
        var end = range.EndBefore;
        // SeriesAsync's filter and order; a lookup keeps each item's rows in that order.
        var rows = (await db.Valuations.AsNoTracking()
            .Where(r => ids.Contains(r.ItemId) && r.RetiredAt == null && r.AsOf >= range.Start && (end == null || r.AsOf < end))
            .OrderBy(r => r.AsOf)
            .ToListAsync(ct)).ToLookup(r => r.ItemId);
        return Result.Success<IReadOnlyDictionary<ItemId, IReadOnlyList<Valuation>>>(
            ids.ToDictionary(id => new ItemId(id), id => (IReadOnlyList<Valuation>)rows[id].Select(ToValuation).ToList()));
    }

    internal static Valuation ToValuation(ValuationRow row)
    {
        if (!Currency.TryCreate(row.Currency.Trim(), out var currency) || !Money.Create(row.Amount, currency).TryGetValue(out var value))
            throw new InvalidOperationException("A stored value has an invalid amount or currency.");
        return new Valuation(row.Id, new ItemId(row.ItemId), row.AsOf, value, row.Note);
    }

    private static Failure FailureOf<T>(Result<T> result) where T : notnull
        => result.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException("Not a failure.");
}
