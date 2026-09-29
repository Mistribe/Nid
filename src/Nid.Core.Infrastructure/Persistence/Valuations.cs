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
        if (current is not null)
        {
            current.RetiredAt = now;
            await db.SaveChangesAsync(ct); // retired first: the insert below would break ux on (item_id, as_of)
        }

        var row = new ValuationRow
        {
            Id = Guid.CreateVersion7(),
            ItemId = valuation.Item.Value,
            AsOf = valuation.AsOf,
            Amount = valuation.Value.Amount,
            Currency = w!.Currency.Code,
            Note = valuation.Note,
            Source = valuation.Source == FactSource.Import ? "import" : "manual",
            CreatedVia = w.Via,
            CreatedByPersonId = w.Person.Value,
            CreatedAt = now,
            SupersedesId = current?.Id,
        };
        db.Valuations.Add(row);
        await db.SaveChangesAsync(ct);
        return Result.Success(ToValuation(row));
    }, ct);

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

    private static Valuation ToValuation(ValuationRow row)
    {
        if (!Currency.TryCreate(row.Currency.Trim(), out var currency) || !Money.Create(row.Amount, currency).TryGetValue(out var value))
            throw new InvalidOperationException("A stored value has an invalid amount or currency.");
        return new Valuation(row.Id, new ItemId(row.ItemId), row.AsOf, value, row.Note);
    }

    private static Failure FailureOf<T>(Result<T> result) where T : notnull
        => result.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException("Not a failure.");
}
