using Microsoft.EntityFrameworkCore;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using NodaTime;

namespace Nid.Banking.Infrastructure.Persistence;

public sealed class AccountReadLog(BankingDbContext db) : IAccountReadLog
{
    public async Task RecordAsync(BankRead read, CancellationToken ct)
    {
        db.AccountReads.Add(new AccountReadRow
        {
            Id = Guid.CreateVersion7(), AccountId = read.Account, ReadAt = read.At, Attended = read.Attended, RateLimited = read.RateLimited,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<BankRead>> SinceAsync(IReadOnlyCollection<Guid>? accounts, Instant since, CancellationToken ct)
    {
        var query = db.AccountReads.AsNoTracking().Where(r => r.ReadAt >= since);
        if (accounts is not null)
            query = query.Where(r => accounts.Contains(r.AccountId));
        return (await query.ToListAsync(ct)).Select(r => new BankRead(r.AccountId, r.ReadAt, r.Attended, r.RateLimited)).ToList();
    }

    public async Task PurgeBeforeAsync(Instant before, CancellationToken ct)
        => await db.AccountReads.Where(r => r.ReadAt < before).ExecuteDeleteAsync(ct);
}
