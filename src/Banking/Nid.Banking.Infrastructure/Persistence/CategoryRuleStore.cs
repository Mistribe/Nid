using Microsoft.EntityFrameworkCore;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Banking.Infrastructure.Persistence;

public sealed class CategoryRuleStore(BankingDbContext db, IClock clock) : ICategoryRuleStore
{
    public async Task<IReadOnlyList<CategoryRule>> AllAsync(CancellationToken ct)
        => (await db.CategoryRules.AsNoTracking().OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct)).Select(ToModel).ToList();

    public async Task<CategoryRule?> FindAsync(Guid id, CancellationToken ct)
        => await db.CategoryRules.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct) is { } row ? ToModel(row) : null;

    public async Task<CategoryRule> CreateAsync(CategoryRule draft, CancellationToken ct)
    {
        var row = new CategoryRuleRow { Id = Guid.CreateVersion7(), CreatedByPersonId = draft.CreatedBy.Value, CreatedAt = clock.GetCurrentInstant() };
        Fill(row, draft);
        db.CategoryRules.Add(row);
        await db.SaveChangesAsync(ct);
        return ToModel(row);
    }

    public async Task<CategoryRule?> UpdateAsync(CategoryRule rule, CancellationToken ct)
    {
        if (await db.CategoryRules.SingleOrDefaultAsync(r => r.Id == rule.Id, ct) is not { } row)
            return null;
        Fill(row, rule);
        await db.SaveChangesAsync(ct);
        return ToModel(row);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        if (await db.CategoryRules.SingleOrDefaultAsync(r => r.Id == id, ct) is not { } row)
            return false;
        db.CategoryRules.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // Bookkeeping, not a decision: unaudited, like the link rules' last_used_at.
    public async Task TouchAsync(IReadOnlyCollection<Guid> ids, Instant at, CancellationToken ct)
        => await db.CategoryRules.Where(r => ids.Contains(r.Id)).ExecuteUpdateAsync(s => s.SetProperty(r => r.LastUsedAt, at), ct);

    private static void Fill(CategoryRuleRow row, CategoryRule rule)
    {
        row.Pattern = rule.Pattern;
        row.AccountId = rule.Account;
        row.Direction = rule.Direction is { } direction ? CategoryStore.Wire(direction) : null;
        row.MinAmount = rule.MinAmount;
        row.MaxAmount = rule.MaxAmount;
        row.CategoryId = rule.Category;
    }

    private static CategoryRule ToModel(CategoryRuleRow r) => new(
        r.Id, r.Pattern, r.AccountId, r.Direction is { } d ? Enum.Parse<RuleDirection>(d, ignoreCase: true) : null, r.MinAmount, r.MaxAmount,
        r.CategoryId, new PersonId(r.CreatedByPersonId), r.CreatedAt, r.LastUsedAt);
}
