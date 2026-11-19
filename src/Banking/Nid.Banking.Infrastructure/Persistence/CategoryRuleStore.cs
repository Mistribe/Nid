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

    public async Task<IReadOnlyDictionary<Guid, (int Lines, Instant? LastUsed)>> UsageAsync(IReadOnlyCollection<Guid> accounts, CancellationToken ct)
        => await db.Transactions.Where(t => t.CategoryRuleId != null && accounts.Contains(t.AccountId))
            .GroupBy(t => t.CategoryRuleId!.Value).Select(g => new { g.Key, Count = g.Count(), Last = g.Max(t => t.CategorisedAt) })
            .ToDictionaryAsync(g => g.Key, g => (g.Count, g.Last), ct);

    public async Task<IReadOnlyList<BankTransaction>> CandidatesAsync(CategoryRule rule, IReadOnlyCollection<Guid> accounts, CancellationToken ct)
    {
        var label = LikePattern.Contains(rule.Pattern);
        var counterparty = Loose(rule.Pattern);
        var query = db.Transactions.AsNoTracking()
            .Where(t => accounts.Contains(t.AccountId) && t.LinkState != "ignored"
                && (t.CategorySource == null || t.CategorySource == "rule" || t.CategorySource == "hint")
                && (t.CategoryId == null || t.CategoryId != rule.Category)
                && (EF.Functions.Like(t.NormalizedLabel, label, @"\")
                    || (t.Counterparty != null && EF.Functions.ILike(t.Counterparty, counterparty, @"\"))));
        if (rule.Direction is { } direction)
            query = direction == RuleDirection.Debit ? query.Where(t => t.Amount < 0) : query.Where(t => t.Amount >= 0);
        if (rule.MinAmount is { } min)
            query = query.Where(t => t.Amount >= min || t.Amount <= -min);
        if (rule.MaxAmount is { } max)
            query = query.Where(t => t.Amount <= max && t.Amount >= -max);
        return (await query
            .OrderByDescending(t => t.BookedOn).ThenByDescending(t => t.Id)
            .ToListAsync(ct)).Select(TransactionStore.ToModel).ToList();
    }

    /// <summary>
    /// The pattern against a raw counterparty, which Normalize has not folded: a vowel, C, N or Y (where É, Ç, Ñ, Ÿ… fold to) and a space
    /// (several of them, or a card number Normalize drops) match loosely; <see cref="Nid.Banking.Domain.CategoryRules.Matches"/> then decides.
    /// </summary>
    // ponytail: misses other accented consonants (Š, Ž, Ł…) and decomposed text (a letter plus its combining accent), and a pattern of
    // vowels barely narrows; upgrade with a normalized_counterparty column written at import, matched like normalized_label.
    private static string Loose(string pattern) => $"%{string.Concat(pattern.Select(c => c switch
    {
        ' ' => "%",
        'A' or 'C' or 'E' or 'I' or 'N' or 'O' or 'U' or 'Y' => "_",
        '%' or '_' or '\\' => $@"\{c}",
        _ => c.ToString(),
    }))}%";

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
