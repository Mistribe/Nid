using Microsoft.EntityFrameworkCore;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using NodaTime;

namespace Nid.Banking.Infrastructure.Persistence;

public sealed class BalanceStore(BankingDbContext db) : IBalanceStore
{
    public async Task UpsertAsync(BankBalance balance, CancellationToken ct)
    {
        var row = await db.Balances.FindAsync([balance.Account, balance.AsOf], ct);
        if (row is null)
            db.Balances.Add(row = new BalanceRow { AccountId = balance.Account, AsOf = balance.AsOf });
        row.Amount = balance.Amount;
        row.BalanceType = balance.BalanceType;
        row.FetchedAt = balance.FetchedAt;
        await db.SaveChangesAsync(ct);
    }

    public async Task<BankBalance?> LatestAsync(Guid account, CancellationToken ct)
        => await db.Balances.AsNoTracking().Where(b => b.AccountId == account).OrderByDescending(b => b.AsOf).FirstOrDefaultAsync(ct) is { } b
            ? new BankBalance(b.AccountId, b.AsOf, b.Amount, b.BalanceType, b.FetchedAt)
            : null;

    public async Task<IReadOnlyList<BankBalance>> InMonthAsync(Guid account, LocalDate monthEnd, CancellationToken ct)
    {
        var first = new LocalDate(monthEnd.Year, monthEnd.Month, 1);
        var next = first.PlusMonths(1);
        return (await db.Balances.AsNoTracking()
                .Where(b => b.AccountId == account && b.AsOf >= first && b.AsOf < next)
                .OrderBy(b => b.AsOf)
                .ToListAsync(ct))
            .Select(b => new BankBalance(b.AccountId, b.AsOf, b.Amount, b.BalanceType, b.FetchedAt)).ToList();
    }
}
