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

    public async Task<IReadOnlyList<GuessableLine>> GuessableAsync(IReadOnlyCollection<Guid> accounts, DecideKey? unguessed, int limit, CancellationToken ct)
    {
        var query = Open(accounts).Where(t => t.LinkState != "internal");
        if (unguessed is { } key)
            query = query.Where(t => !Guesses(key).Any(g => g.Pattern == t.SuggestedPattern && g.Debit == (t.Amount < 0)));
        return (await query.OrderByDescending(t => t.BookedOn).ThenByDescending(t => t.Id).Take(limit)
                .Select(t => new { t.SuggestedPattern, Debit = t.Amount < 0, t.AccountId, t.Label, t.Counterparty }).ToListAsync(ct))
            .Select(t => new GuessableLine(t.SuggestedPattern!, t.Debit, t.AccountId, t.Label, t.Counterparty)).ToList();
    }

    public async Task<IReadOnlyList<CategoryGuess>> GuessesAsync(DecideKey key, CancellationToken ct)
        => (await Guesses(key).AsNoTracking().ToListAsync(ct))
            .Select(g => new CategoryGuess(g.Pattern, g.Debit, g.CategoryId, g.Probability, key, g.Placement, g.DecidedAt)).ToList();

    public async Task SaveGuessesAsync(IReadOnlyList<CategoryGuess> guesses, CancellationToken ct)
    {
        foreach (var guess in guesses)
        {
            var key = guess.Key;
            var row = await db.CategoryGuesses.SingleOrDefaultAsync(g => g.Pattern == guess.Pattern && g.Debit == guess.Debit && g.Model == key.Model
                && g.Digest == key.Digest, ct);
            if (row is null)
                db.CategoryGuesses.Add(row = new CategoryGuessRow { Pattern = guess.Pattern, Debit = guess.Debit, Model = key.Model, Digest = key.Digest });
            (row.CategoryId, row.Probability, row.Implementation, row.Version, row.Placement, row.DecidedAt)
                = (guess.Category, guess.Probability, key.Implementation, key.Version, guess.Placement, guess.DecidedAt);
        }
        await db.SaveChangesAsync(ct);
    }

    // A model's answers, not a decision: excluded from the log, like the embeddings.
    public Task<int> ForgetGuessesAsync(CancellationToken ct) => db.CategoryGuesses.ExecuteDeleteAsync(ct);

    private IQueryable<CategoryGuessRow> Guesses(DecideKey key)
        => db.CategoryGuesses.Where(g => g.Model == key.Model && g.Digest == key.Digest && g.Implementation == key.Implementation && g.Version == key.Version);

    private static SuggestionDismissal ToModel(SuggestionDismissalRow r) => new(
        r.Id, r.Pattern, Enum.Parse<RuleDirection>(r.Direction, ignoreCase: true), r.CategoryId, new PersonId(r.CreatedByPersonId), r.CreatedAt);
}
