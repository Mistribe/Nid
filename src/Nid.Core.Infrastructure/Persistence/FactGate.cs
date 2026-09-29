using Microsoft.EntityFrameworkCore;
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

    /// <summary>The fact's own currency and date, once the item is known.</summary>
    public async Task<Result> CheckAsync(FactWriter writer, Money amount, LocalDate date, CancellationToken ct)
    {
        if (amount.Currency != writer.Currency)
            return Fail(CoreFactCodes.CurrencyMismatch, $"This item's facts are in {writer.Currency}.");
        if (date > await calendar.TodayAsync(ct))
            return Fail(CoreFactCodes.DateInFuture, "A value or a flow is not dated after today.");
        return Result.Success();
    }

    /// <summary>
    /// Runs <paramref name="work"/> in the caller's transaction, or in one of its own. A unique violation or a lost
    /// concurrency check is <see cref="CoreFactCodes.FactConflict"/>: the rows it left tracked are detached, and a
    /// transaction of its own is rolled back. The caller's transaction is then unusable, as the contracts say.
    /// </summary>
    public async Task<Result<T>> WriteAsync<T>(Func<Task<Result<T>>> work, CancellationToken ct) where T : notnull
    {
        await using var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            var result = await work();
            if (own is not null && result.IsSuccess)
                await own.CommitAsync(ct);
            else
                Detach();
            return result;
        }
        catch (DbUpdateException e) when (e is DbUpdateConcurrencyException
            || e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            Detach();
            return Result.Failure<T>(new Failure(CoreFactCodes.FactConflict, "Someone else changed this item's facts at the same time; try again.", null));
        }
    }

    /// <summary>Forgets every fact row the context tracks, so a refused write leaves nothing for the next save.</summary>
    public void Detach()
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
