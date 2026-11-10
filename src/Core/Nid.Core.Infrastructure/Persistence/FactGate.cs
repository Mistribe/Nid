using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Npgsql;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Infrastructure.Persistence;

/// <summary>Who writes a fact, in which currency: what <see cref="FactGate.ForWriteAsync"/> establishes.</summary>
public sealed record FactWriter(PersonId Person, string Via, Currency Currency);

/// <summary>
/// 2A spec §5, the checks both stores share, in the spec's order: the actor is a person who reads, then writes, the
/// item; the item has a currency; the fact is in it; its date is not after the household's today.
/// </summary>
public sealed class FactGate(CoreDbContext db, IAccessPolicy policy, IActorAccessor actors, IHouseholdCalendar calendar)
{
    public Task<Result<FactWriter>> ForWriteAsync(ItemId item, CancellationToken ct) => ForAsync(item, AccessKind.Write, ct);

    public async Task<Result<Currency>> ForReadAsync(ItemId item, CancellationToken ct)
    {
        var access = await ForAsync(item, AccessKind.Read, ct);
        if (access.TryGetFailure(out var denied))
            return Result.Failure<Currency>(denied!);
        access.TryGetValue(out var reader);
        return Result.Success(reader!.Currency);
    }

    /// <summary>
    /// Batched reads spec §2: <see cref="ForReadAsync"/> for many items as a block, in four queries whatever their number. The
    /// first item the actor cannot read (or that does not exist) fails the whole read as it fails alone — existence is not
    /// disclosed — then any item without a currency.
    /// </summary>
    public async Task<Result> ForReadManyAsync(IReadOnlyCollection<ItemId> items, CancellationToken ct)
    {
        if (actors.Current is not { PersonId: { } person })
            return Result.Failure(new UnauthorizedFailure("Values and flows are written and read by a person.", null));
        if (items.Count == 0)
            return Result.Success();

        var readable = await policy.CheckManyAsync(person, items, AccessKind.Read, ct);
        foreach (var item in items)
            if (!readable.Contains(item))
                return Result.Failure(new NotFoundFailure("item", item.ToString()));

        var ids = items.Select(i => i.Value).Distinct().ToList();
        var codes = await db.Items.AsNoTracking().Where(i => ids.Contains(i.Id)).Select(i => i.Currency).ToListAsync(ct);
        return codes.All(code => Currency.TryCreate(code?.Trim(), out _))
            ? Result.Success()
            : Fail(CoreFactCodes.ItemWithoutCurrency, "This item takes no values or flows: it has no currency.");
    }

    /// <summary>The fact's own currency and date, once the item is known.</summary>
    public async Task<Result> CheckAsync(FactWriter writer, Money amount, LocalDate date, CancellationToken ct)
        => Check(writer, amount, date, await TodayAsync(ct));

    /// <summary>The household's today, read once for a write of several facts.</summary>
    public async Task<LocalDate> TodayAsync(CancellationToken ct) => await calendar.TodayAsync(ct);

    /// <summary><see cref="CheckAsync"/> against a <paramref name="today"/> already read.</summary>
    public static Result Check(FactWriter writer, Money amount, LocalDate date, LocalDate today)
    {
        if (amount.Currency != writer.Currency)
            return Fail(CoreFactCodes.CurrencyMismatch, $"This item's facts are in {writer.Currency}.");
        if (date > today)
            return Fail(CoreFactCodes.DateInFuture, "A value or a flow is not dated after today.");
        return Result.Success();
    }

    /// <summary>
    /// Runs <paramref name="work"/> in the caller's transaction, under a savepoint, or in a transaction of its own. A
    /// unique violation or a lost concurrency check is <see cref="CoreFactCodes.FactConflict"/>. On any other outcome
    /// than success — a failure result, a conflict, an exception — everything <paramref name="work"/> saved is undone
    /// (back to the savepoint, or its own transaction rolled back) and the fact rows it left tracked are detached, so
    /// nothing of it stays in the caller's transaction or reaches the caller's next save. On success the rows stay
    /// tracked, as saved.
    /// </summary>
    /// <remarks>
    /// The savepoint is what makes a supersede atomic inside a unit of work: EF Core only rolls a failed save back to
    /// its own savepoint, which would keep the retirement saved just before, and its audit line, in the caller's
    /// still-usable transaction.
    /// </remarks>
    public async Task<Result<T>> WriteAsync<T>(Func<Task<Result<T>>> work, CancellationToken ct) where T : notnull
    {
        var outer = db.Database.CurrentTransaction;
        await using var own = outer is null ? await db.Database.BeginTransactionAsync(ct) : null;
        if (outer is not null)
            await outer.CreateSavepointAsync(Savepoint, ct);

        Result<T> result;
        try
        {
            result = await work();
            if (result.IsSuccess)
            {
                if (own is not null)
                    await own.CommitAsync(ct);
                else
                    await outer!.ReleaseSavepointAsync(Savepoint, ct);
                return result;
            }
        }
        catch (DbUpdateException e) when (e is DbUpdateConcurrencyException
            || e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            result = Result.Failure<T>(new Failure(CoreFactCodes.FactConflict, "Someone else changed this item's facts at the same time; try again.", null));
        }
        catch
        {
            await UndoAsync(outer, unwinding: true);
            throw;
        }

        await UndoAsync(outer, unwinding: false);
        return result;
    }

    private const string Savepoint = "fact";

    /// <summary>A transaction of its own is rolled back by its disposal; the caller's goes back to the savepoint.</summary>
    private async Task UndoAsync(IDbContextTransaction? outer, bool unwinding)
    {
        Detach();
        if (outer is null)
            return;
        try
        {
            await outer.RollbackToSavepointAsync(Savepoint, CancellationToken.None);
        }
        catch when (unwinding)
        {
            // The exception already unwinding is the one the caller must see; a broken connection fails both.
        }
    }

    /// <summary>Forgets every fact row the context tracks, so a refused write leaves nothing for the next save.</summary>
    private void Detach()
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity is ValuationRow or FlowRow).ToList())
            entry.State = EntityState.Detached;
    }

    public static Result Fail(string code, string message) => Result.Failure(new Failure(code, message, null));

    private async Task<Result<FactWriter>> ForAsync(ItemId item, AccessKind kind, CancellationToken ct)
    {
        if (actors.Current is not { PersonId: { } person } actor)
            return Result.Failure<FactWriter>(new UnauthorizedFailure("Values and flows are written and read by a person.", null));

        var access = await policy.CheckAsync(person, item, kind, ct);
        if (access.TryGetFailure(out var denied))
            return Result.Failure<FactWriter>(denied!);

        var code = await db.Items.AsNoTracking().Where(i => i.Id == item.Value).Select(i => i.Currency).SingleAsync(ct);
        if (!Currency.TryCreate(code?.Trim(), out var currency))
            return Result.Failure<FactWriter>(new Failure(CoreFactCodes.ItemWithoutCurrency, "This item takes no values or flows: it has no currency.", null));

        return Result.Success(new FactWriter(person, actor.Via.ToWire(), currency));
    }
}
