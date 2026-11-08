using Microsoft.EntityFrameworkCore;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Banking.Infrastructure.Persistence;

public sealed class RuleStore(BankingDbContext db, IClock clock) : IRuleStore
{
    public async Task<IReadOnlyList<BankRule>> ForAccountAsync(Guid account, CancellationToken ct)
        => (await db.Rules.AsNoTracking().Where(r => r.AccountId == account).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct)).Select(ToModel).ToList();

    public async Task<IReadOnlyList<BankRule>> ByAsync(PersonId creator, CancellationToken ct)
        => (await db.Rules.AsNoTracking().Where(r => r.CreatedByPersonId == creator.Value).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct))
            .Select(ToModel).ToList();

    public async Task<BankRule> UpsertAsync(Guid account, string stem, string module, ItemId target, PersonId by, CancellationToken ct)
    {
        var row = await db.Rules.FirstOrDefaultAsync(r => r.AccountId == account && r.CreatedByPersonId == by.Value && r.LabelStem == stem, ct);
        if (row is null)
            db.Rules.Add(row = new RuleRow
            {
                Id = Guid.CreateVersion7(), AccountId = account, LabelStem = stem, CreatedByPersonId = by.Value, CreatedAt = clock.GetCurrentInstant(),
            });

        row.TargetModule = module;
        row.TargetItemId = target.Value;
        await db.SaveChangesAsync(ct);
        return ToModel(row);
    }

    public async Task<bool> DeleteAsync(Guid id, PersonId creator, CancellationToken ct)
    {
        var row = await db.Rules.FirstOrDefaultAsync(r => r.Id == id && r.CreatedByPersonId == creator.Value, ct);
        if (row is null)
            return false;

        db.Rules.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // Bookkeeping, not a decision: unaudited, like the sync's last_valued_on.
    public async Task TouchAsync(Guid id, Instant at, CancellationToken ct)
        => await db.Rules.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.LastUsedAt, at), ct);

    public async Task<bool> TryRememberGroupAsync(Guid account, string stem, Instant at, CancellationToken ct)
        => await db.Database.ExecuteSqlAsync(
            $"insert into banking.recurring_group (id, account_id, label_stem, proposed_at) values ({Guid.CreateVersion7()}, {account}, {stem}, {at}) on conflict (account_id, label_stem) do nothing",
            ct) == 1;

    private static BankRule ToModel(RuleRow r) => new(
        r.Id, r.AccountId, r.LabelStem, r.TargetModule, new ItemId(r.TargetItemId), new PersonId(r.CreatedByPersonId), r.CreatedAt, r.LastUsedAt);
}
