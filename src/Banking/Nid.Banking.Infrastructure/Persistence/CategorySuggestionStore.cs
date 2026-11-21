using Microsoft.EntityFrameworkCore;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Banking.Infrastructure.Persistence;

public sealed class CategorySuggestionStore(BankingDbContext db, IClock clock) : ICategorySuggestionStore
{
    public async Task<IReadOnlyList<CategorySuggestions.Evidence>> EvidenceAsync(IReadOnlyCollection<Guid> accounts, CancellationToken ct)
        => (await db.Transactions.Where(t => accounts.Contains(t.AccountId) && t.SuggestedPattern != null && t.CategoryId != null
                && (t.CategorySource == "hand" || t.CategorySource == "assistant"))
            .GroupBy(t => new { t.SuggestedPattern, Debit = t.Amount < 0, t.CategoryId })
            .Select(g => new { g.Key.SuggestedPattern, g.Key.Debit, g.Key.CategoryId, Lines = g.Count() })
            .ToListAsync(ct))
            .Select(g => new CategorySuggestions.Evidence(g.SuggestedPattern!, g.Debit, g.CategoryId!.Value, g.Lines)).ToList();

    public async Task<IReadOnlyList<(string Pattern, bool Debit, int Lines)>> OpenAsync(IReadOnlyCollection<Guid> accounts, CancellationToken ct)
        => (await Open(accounts).GroupBy(t => new { t.SuggestedPattern, Debit = t.Amount < 0 })
            .Select(g => new { g.Key.SuggestedPattern, g.Key.Debit, Lines = g.Count() }).ToListAsync(ct))
            .Select(g => (g.SuggestedPattern!, g.Debit, g.Lines)).ToList();

    public async Task<IReadOnlyList<BankTransaction>> OpenLinesAsync(IReadOnlyCollection<Guid> accounts, string pattern, bool debit, int take, CancellationToken ct)
        => (await Open(accounts).Where(t => t.SuggestedPattern == pattern && (t.Amount < 0) == debit)
            .OrderByDescending(t => t.BookedOn).ThenByDescending(t => t.Id).Take(take).ToListAsync(ct)).Select(TransactionStore.ToModel).ToList();

    /// <summary>
    /// A rule in use matches a line as <see cref="CategoryRules.Matches"/> does, the counterparty folded by <c>upper</c> and <c>unaccent</c>
    /// rather than Normalize.
    /// </summary>
    // ponytail: any rule in use counts, even one whose author does not write the line's account (the pass would not apply it there): such a
    // line gets no suggestion. Upgrade by passing each account's writers if households report it.
    private IQueryable<TransactionRow> Open(IReadOnlyCollection<Guid> accounts)
        => db.Transactions.AsNoTracking().Where(t => accounts.Contains(t.AccountId) && t.SuggestedPattern != null
            && t.CategoryId == null && t.CategorySource == null && t.LinkState != "ignored"
            && !db.CategoryRules.Any(r => (r.AccountId == null || r.AccountId == t.AccountId)
                && (r.Direction == null || (r.Direction == "debit") == (t.Amount < 0))
                && (r.MinAmount == null || t.Amount >= r.MinAmount || t.Amount <= -r.MinAmount)
                && (r.MaxAmount == null || (t.Amount <= r.MaxAmount && t.Amount >= -r.MaxAmount))
                && db.Categories.Any(c => c.Id == r.CategoryId && c.ArchivedAt == null)
                && (t.NormalizedLabel.Contains(r.Pattern)
                    || (t.Counterparty != null && EF.Functions.Unaccent(t.Counterparty.ToUpper()).Contains(r.Pattern)))));

    public async Task<IReadOnlyList<SuggestionDismissal>> DismissalsAsync(CancellationToken ct, IReadOnlyCollection<Guid>? readable = null, PersonId? author = null)
    {
        var query = db.SuggestionDismissals.AsNoTracking();
        if (readable is not null)
        {
            // A dismissal names a pattern: shown to its author, or to who reads a line that has it.
            var by = author?.Value;
            query = query.Where(d => d.CreatedByPersonId == by
                || db.Transactions.Any(t => readable.Contains(t.AccountId) && t.SuggestedPattern == d.Pattern));
        }
        return (await query.OrderBy(d => d.Pattern).ThenBy(d => d.Direction).ThenBy(d => d.Id).ToListAsync(ct)).Select(ToModel).ToList();
    }

    public async Task<SuggestionDismissal> DismissAsync(string pattern, RuleDirection direction, Guid category, PersonId by, CancellationToken ct)
    {
        var wire = CategoryStore.Wire(direction);
        if (await db.SuggestionDismissals.AsNoTracking().SingleOrDefaultAsync(d => d.Pattern == pattern && d.Direction == wire && d.CategoryId == category, ct) is { } found)
            return ToModel(found);
        var row = new SuggestionDismissalRow
        {
            Id = Guid.CreateVersion7(), Pattern = pattern, Direction = wire, CategoryId = category, CreatedByPersonId = by.Value,
            CreatedAt = clock.GetCurrentInstant(),
        };
        db.SuggestionDismissals.Add(row);
        await db.SaveChangesAsync(ct);
        return ToModel(row);
    }

    public async Task<bool> WithdrawAsync(Guid id, CancellationToken ct)
    {
        if (await db.SuggestionDismissals.SingleOrDefaultAsync(d => d.Id == id, ct) is not { } row)
            return false;
        db.SuggestionDismissals.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static SuggestionDismissal ToModel(SuggestionDismissalRow r) => new(
        r.Id, r.Pattern, Enum.Parse<RuleDirection>(r.Direction, ignoreCase: true), r.CategoryId, new PersonId(r.CreatedByPersonId), r.CreatedAt);
}
