using Microsoft.EntityFrameworkCore;
using Nid.Banking.Application;
using Nid.Banking.Contracts;

namespace Nid.Banking.Infrastructure.Persistence;

public sealed class CategoryBudgetStore(BankingDbContext db) : ICategoryBudgetStore
{
    public async Task<IReadOnlyList<CategoryBudget>> AllAsync(CancellationToken ct)
        => (await db.CategoryBudgets.AsNoTracking().ToListAsync(ct)).Select(r => new CategoryBudget(
            r.CategoryId, r.Amount, Enum.Parse<BudgetPeriod>(r.Period, ignoreCase: true))).ToList();

    public async Task SetAsync(CategoryBudget budget, CancellationToken ct)
    {
        if (await db.CategoryBudgets.SingleOrDefaultAsync(b => b.CategoryId == budget.Category, ct) is not { } row)
            db.CategoryBudgets.Add(row = new CategoryBudgetRow { Id = Guid.CreateVersion7(), CategoryId = budget.Category });
        row.Amount = budget.Amount;
        row.Period = CategoryStore.Wire(budget.Period);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid category, CancellationToken ct)
    {
        if (await db.CategoryBudgets.SingleOrDefaultAsync(b => b.CategoryId == category, ct) is not { } row)
            return false;
        db.CategoryBudgets.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
