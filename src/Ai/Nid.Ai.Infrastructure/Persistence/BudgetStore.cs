using Microsoft.EntityFrameworkCore;
using Nid.Ai.Application;
using NodaTime;

namespace Nid.Ai.Infrastructure.Persistence;

/// <summary>
/// The budget's counters (#488, <c>docs/ingestion/llm-providers.md</c> §7). A reservation adds to <c>reserved</c> in one
/// statement that checks the ceiling, so two calls at once never pass it together; settling moves the amount from
/// <c>reserved</c> to <c>spent</c>.
/// </summary>
public sealed class BudgetStore(AiDbContext db) : IBudget
{
    public async Task<decimal> CapAsync(CancellationToken ct)
        => await db.Settings.AsNoTracking().Where(s => s.Id == 1).Select(s => s.MonthlyBudgetEur).SingleOrDefaultAsync(ct);

    public async Task SetCapAsync(decimal cap, CancellationToken ct)
    {
        var settings = await db.Settings.SingleOrDefaultAsync(s => s.Id == 1, ct);
        if (settings is null)
            db.Settings.Add(settings = new AiSettingsRow { Id = 1 });
        settings.MonthlyBudgetEur = cap;
        await db.SaveChangesAsync(ct);
    }

    public async Task<BudgetState> StateAsync(LocalDate month, CancellationToken ct)
    {
        var row = await db.BudgetMonths.AsNoTracking().SingleOrDefaultAsync(b => b.Month == month, ct);
        return new BudgetState(month, await CapAsync(ct), row?.Spent ?? 0, row?.Reserved ?? 0);
    }

    public async Task<Guid?> ReserveAsync(LocalDate month, decimal amount, Instant expires, CancellationToken ct)
    {
        var cap = await CapAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"insert into ai.budget_month (month, spent, reserved) values ({month}, 0, 0) on conflict (month) do nothing", ct);
        // The check and the hold in one statement: the ceiling holds whatever runs at the same time.
        var held = await db.BudgetMonths.Where(b => b.Month == month && b.Spent + b.Reserved + amount <= cap)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Reserved, b => b.Reserved + amount), ct);
        if (held == 0)
            return null;

        var reservation = new BudgetReservationRow { Id = Guid.CreateVersion7(), Month = month, Amount = amount, ExpiresAt = expires };
        db.BudgetReservations.Add(reservation);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return reservation.Id;
    }

    public async Task<BudgetSettlement?> SettleAsync(Guid reservation, decimal actual, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.BudgetReservations.SingleOrDefaultAsync(r => r.Id == reservation, ct);
        if (row is null)
            return null; // already settled (by the sweep)
        var settled = await SettleAsync(row, actual, ct);
        await tx.CommitAsync(ct);
        return settled;
    }

    public async Task<IReadOnlyList<BudgetSettlement>> SettleExpiredAsync(Instant now, CancellationToken ct)
    {
        var settled = new List<BudgetSettlement>();
        foreach (var id in await db.BudgetReservations.AsNoTracking().Where(r => r.ExpiresAt <= now).Select(r => r.Id).ToListAsync(ct))
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (await db.BudgetReservations.SingleOrDefaultAsync(r => r.Id == id, ct) is { } row)
                settled.Add(await SettleAsync(row, row.Amount, ct));
            await tx.CommitAsync(ct);
        }

        return settled;
    }

    private async Task<BudgetSettlement> SettleAsync(BudgetReservationRow row, decimal actual, CancellationToken ct)
    {
        var before = await db.BudgetMonths.AsNoTracking().Where(b => b.Month == row.Month).Select(b => b.Spent).SingleAsync(ct);
        await db.BudgetMonths.Where(b => b.Month == row.Month)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Spent, b => b.Spent + actual).SetProperty(b => b.Reserved, b => b.Reserved - row.Amount), ct);
        db.BudgetReservations.Remove(row);
        await db.SaveChangesAsync(ct);
        return new BudgetSettlement(row.Month, before, before + actual, await CapAsync(ct));
    }
}
