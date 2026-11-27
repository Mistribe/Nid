using Microsoft.EntityFrameworkCore;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Banking.Infrastructure.Persistence;

public sealed class CategoryStore(BankingDbContext db, IClock clock) : ICategoryStore
{
    public const int MaxName = 60;

    public async Task<IReadOnlyList<BankCategory>> AllAsync(CancellationToken ct)
        => (await db.Categories.AsNoTracking().OrderBy(c => c.ParentId != null).ThenBy(c => c.Name).ThenBy(c => c.Id).ToListAsync(ct)).Select(ToModel).ToList();

    public async Task<BankCategory?> FindAsync(Guid id, CancellationToken ct)
        => await db.Categories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) is { } row ? ToModel(row) : null;

    public async Task<Guid?> ResolveKeyAsync(string key, CancellationToken ct)
    {
        var merges = await db.Categories.AsNoTracking().Select(c => new { c.Id, c.Key, c.MergedIntoId }).ToListAsync(ct);
        var current = merges.SingleOrDefault(c => c.Key == key);
        // A merge never targets a category merged before it, but a later merge may: follow the chain, a few links at most.
        for (var hops = 0; current?.MergedIntoId is { } next && hops < merges.Count; hops++)
            current = merges.Single(c => c.Id == next);
        return current?.Id;
    }

    public async Task<Result<BankCategory>> CreateAsync(Guid? parent, string name, CategoryKind kind, CancellationToken ct)
    {
        name = name.Trim();
        if (name.Length is 0 or > MaxName)
            return NameError(InvalidName);

        if (parent is { } parentId)
        {
            if (await db.Categories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == parentId, ct) is not { } parentRow)
                return NotFound(parentId);
            if (parentRow.ParentId is not null)
                return Fail(BankingCodes.CategoryDepth, "A category under a child: categories are two levels deep.");
            if (parentRow.ArchivedAt is not null)
                return Fail(BankingCodes.CategoryArchived, "A new category under an archived one.");
            kind = Kind(parentRow.Kind);
        }

        if (await SiblingNamedAsync(parent, name, null, ct))
            return NameError("Another category of the same parent has this name.");

        var row = new CategoryRow { Id = Guid.CreateVersion7(), ParentId = parent, Name = name, Kind = Wire(kind) };
        db.Categories.Add(row);
        await ForgetGuessesAsync(ct);
        await db.SaveChangesAsync(ct);
        return Result.Success(ToModel(row));
    }

    public async Task<Result<BankCategory>> UpdateAsync(Guid id, string? name, Guid? parent, bool moveParent, bool? archived, CancellationToken ct)
    {
        if (await db.Categories.SingleOrDefaultAsync(c => c.Id == id, ct) is not { } row)
            return NotFound(id);

        var newName = name?.Trim() ?? row.Name;
        if (newName.Length is 0 or > MaxName)
            return NameError(InvalidName);

        // A merged category is a pointer to its target: moving or bringing it back would fork the chain.
        if (row.MergedIntoId is not null && (archived == false || (moveParent && parent != row.ParentId)))
            return Fail(BankingCodes.CategoryArchived, "A merged category is neither moved nor unarchived.");

        var newParent = moveParent ? parent : row.ParentId;
        if (moveParent && parent is { } parentId)
        {
            if (await db.Categories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == parentId, ct) is not { } parentRow)
                return NotFound(parentId);
            if (parentId == id || parentRow.ParentId is not null || await db.Categories.AnyAsync(c => c.ParentId == id && c.MergedIntoId == null, ct))
                return Fail(BankingCodes.CategoryDepth, "Categories are two levels deep: a child has no children.");
            if (parentRow.Kind != row.Kind)
                return Fail(BankingCodes.CategoryKind, "A category moves only under a parent of its kind.");
            if (parentRow.ArchivedAt is not null && parentId != row.ParentId)
                return Fail(BankingCodes.CategoryArchived, "A category moved under an archived one.");
            // The parent is in use here; an archived category's budget is inert and never blocks.
            if (parentId != row.ParentId && row.ArchivedAt is null && await db.CategoryBudgets.CountAsync(b => b.CategoryId == id || b.CategoryId == parentId, ct) == 2)
                return Fail(BankingCodes.BudgetBranch, "A budgeted category moves under no budgeted one: one budget per branch.");
        }

        if ((newName != row.Name || newParent != row.ParentId) && await SiblingNamedAsync(newParent, newName, id, ct))
            return NameError("Another category of the same parent has this name.");

        if (newName != row.Name || newParent != row.ParentId || (archived is { } archiving && archiving != (row.ArchivedAt is not null)))
            await ForgetGuessesAsync(ct);
        row.Name = newName;
        row.ParentId = newParent;
        if (archived == true)
        {
            var now = clock.GetCurrentInstant();
            row.ArchivedAt ??= now;
            foreach (var child in await db.Categories.Where(c => c.ParentId == id && c.ArchivedAt == null).ToListAsync(ct))
                child.ArchivedAt = now;
        }
        else if (archived == false)
        {
            row.ArchivedAt = null;
            if (row.ParentId is { } up && await db.Categories.SingleAsync(c => c.Id == up, ct) is { ArchivedAt: not null } archivedParent)
                archivedParent.ArchivedAt = null;
        }

        await db.SaveChangesAsync(ct);
        return Result.Success(ToModel(row));
    }

    public async Task<Result<BankCategory>> MergeAsync(Guid from, Guid into, CancellationToken ct)
    {
        if (await db.Categories.SingleOrDefaultAsync(c => c.Id == from, ct) is not { } source)
            return NotFound(from);
        if (await db.Categories.SingleOrDefaultAsync(c => c.Id == into, ct) is not { } target)
            return NotFound(into);
        if (source.MergedIntoId is not null || target.MergedIntoId is not null)
            return Fail(BankingCodes.CategoryArchived, "A merged category is merged no further, and nothing merges into it.");
        if (from == into || (target.ParentId is not null && await db.Categories.AnyAsync(c => c.ParentId == from && c.MergedIntoId == null, ct)))
            return Fail(BankingCodes.CategoryDepth, "A category with children merges only into a top-level one, never into itself.");
        if (source.Kind != target.Kind)
            return Fail(BankingCodes.CategoryKind, "A category merges only into one of its kind.");
        if (target.ArchivedAt is not null)
            return Fail(BankingCodes.CategoryArchived, "A category merges only into one in use.");

        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            // Tracked first, so the budgets' check reads the tree as the merge leaves it; the lines move only once it passes.
            var tree = (await db.Categories.ToListAsync(ct)).ToDictionary(c => c.Id);
            var budgets = await db.CategoryBudgets.ToListAsync(ct);
            var lineMoves = new List<(Guid From, Guid To)>();
            await MergeRowAsync(source, target, clock.GetCurrentInstant(), lineMoves, ct);
            await ForgetGuessesAsync(ct);
            // A merged category had no budget before this merge: one hop takes each budget to where its category went.
            foreach (var budget in budgets.Where(b => tree[b.CategoryId].MergedIntoId is not null))
                budget.CategoryId = tree[budget.CategoryId].MergedIntoId!.Value;
            var budgeted = budgets.Select(b => b.CategoryId).ToList();
            // One per category always (the unique index); one per branch among categories in use, an archived one's budget being inert.
            if (budgeted.Count != budgeted.Distinct().Count()
                || budgeted.Any(c => tree[c].ArchivedAt is null && tree[c].ParentId is { } up && tree[up].ArchivedAt is null && budgeted.Contains(up)))
            {
                db.ChangeTracker.Clear();
                return Fail(BankingCodes.BudgetConflict, "Both have a budget, or the merge puts two on one branch: remove one first.");
            }

            foreach (var (moved, to) in lineMoves)
                await db.Transactions.Where(t => t.CategoryId == moved).ExecuteUpdateAsync(s => s.SetProperty(t => t.CategoryId, to), ct);
            foreach (var (moved, to) in lineMoves)
                await db.TransactionParts.Where(p => p.CategoryId == moved).ExecuteUpdateAsync(s => s.SetProperty(p => p.CategoryId, to), ct);
            await db.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return Result.Success(ToModel(source));
        }
        catch
        {
            // Rolled back: the scope's next write must not save the half-merged rows again.
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>#450: a model's guesses chose among the categories as they were: any change of the options drops them all, in the same save,
    /// and the next pass asks again.</summary>
    private async Task ForgetGuessesAsync(CancellationToken ct) => db.CategoryGuesses.RemoveRange(await db.CategoryGuesses.ToListAsync(ct));

    private async Task MergeRowAsync(CategoryRow source, CategoryRow target, Instant at, List<(Guid From, Guid To)> lineMoves, CancellationToken ct)
    {
        lineMoves.Add((source.Id, target.Id));
        // Tracked, so audited: a rule is the household's decision, a line's category is not.
        foreach (var rule in await db.CategoryRules.Where(r => r.CategoryId == source.Id).ToListAsync(ct))
            rule.CategoryId = target.Id;
        // #449: a dismissed suggestion follows its category, unless the target has the same one already.
        foreach (var dismissal in await db.SuggestionDismissals.Where(d => d.CategoryId == source.Id).ToListAsync(ct))
            if (await db.SuggestionDismissals.AnyAsync(d => d.CategoryId == target.Id && d.Pattern == dismissal.Pattern && d.Direction == dismissal.Direction, ct))
                db.SuggestionDismissals.Remove(dismissal);
            else
                dismissal.CategoryId = target.Id;
        // Never into a merged child (its lines would land on a pointer): an active one first, else an archived one.
        var targetChildren = (await db.Categories.Where(c => c.ParentId == target.Id && c.MergedIntoId == null).ToListAsync(ct))
            .OrderBy(c => c.ArchivedAt is not null).ToList();
        foreach (var child in await db.Categories.Where(c => c.ParentId == source.Id).ToListAsync(ct))
        {
            // OrdinalIgnoreCase here, Postgres lower() for sibling uniqueness: the same for French letters.
            if (child.MergedIntoId is null
                && targetChildren.FirstOrDefault(c => string.Equals(c.Name, child.Name, StringComparison.OrdinalIgnoreCase)) is { } same)
                await MergeRowAsync(child, same, at, lineMoves, ct);
            // An already merged child keeps its target; it follows its parent only where it stays two levels deep.
            else if (child.MergedIntoId is null || target.ParentId is null)
                child.ParentId = target.Id;
        }

        source.ArchivedAt ??= at;
        source.MergedIntoId = target.Id;
    }

    public async Task<int> SetForLinesAsync(
        IReadOnlyCollection<Guid> lines, Guid? category, CategorySource? source, string by, Instant at, bool byPerson, CancellationToken ct,
        Guid? ruleId = null)
    {
        // CategoryPrecedence, in the WHERE: one source of truth for which current sources a write replaces. A clear (no source) leaves a
        // person's choice (hand, assistant) to a person.
        var replaceable = Enum.GetValues<CategorySource>()
            .Where(c => source is { } incoming ? CategoryPrecedence.CanReplace(c, incoming, byPerson)
                : c is not CategorySource.Split && (byPerson || c is not (CategorySource.Hand or CategorySource.Assistant)))
            .Select(Wire).ToList();
        var query = db.Transactions.Where(t => lines.Contains(t.Id) && (t.CategorySource == null || replaceable.Contains(t.CategorySource)));
        if (source is null)
            return await query.ExecuteUpdateAsync(s => s
                .SetProperty(t => t.CategoryId, (Guid?)null).SetProperty(t => t.CategorySource, (string?)null)
                .SetProperty(t => t.CategoryRuleId, (Guid?)null).SetProperty(t => t.CategorisedAt, (Instant?)null)
                .SetProperty(t => t.CategorisedBy, (string?)null), ct);

        var wire = Wire(source.Value);
        var rule = source == CategorySource.Rule ? ruleId : null;
        return await query
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.CategoryId, category).SetProperty(t => t.CategorySource, wire)
                .SetProperty(t => t.CategoryRuleId, rule).SetProperty(t => t.CategorisedAt, at)
                .SetProperty(t => t.CategorisedBy, by), ct);
    }

    public Task<int> ClearSourceAsync(IReadOnlyCollection<Guid> lines, IReadOnlyCollection<CategorySource> sources, CancellationToken ct)
        => ClearSourceAsync(db.Transactions.Where(t => lines.Contains(t.Id)), sources, ct);

    /// <summary>Clears the category of the <paramref name="lines"/> whose source is one of <paramref name="sources"/>, in one statement.</summary>
    internal static Task<int> ClearSourceAsync(IQueryable<TransactionRow> lines, IReadOnlyCollection<CategorySource> sources, CancellationToken ct)
    {
        var wires = sources.Select(Wire).ToList();
        return lines.Where(t => t.CategorySource != null && wires.Contains(t.CategorySource))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.CategoryId, (Guid?)null).SetProperty(t => t.CategorySource, (string?)null)
                .SetProperty(t => t.CategoryRuleId, (Guid?)null).SetProperty(t => t.CategorisedAt, (Instant?)null)
                .SetProperty(t => t.CategorisedBy, (string?)null), ct);
    }

    // lower() on both sides, in Postgres: the same folding for the stored name and the new one.
    private Task<bool> SiblingNamedAsync(Guid? parent, string name, Guid? except, CancellationToken ct)
        => db.Categories.AnyAsync(c => c.ParentId == parent && c.MergedIntoId == null && c.Id != except && c.Name.ToLower() == name.ToLower(), ct);

    private const string InvalidName = "A category's name is 1 to 60 characters, and not only whitespace.";

    private static Result<BankCategory> NameError(string message)
        => Result.Failure<BankCategory>(FieldErrors.At(new Failure(BankingCodes.CategoryName, message, null), "name"));

    private static Result<BankCategory> Fail(string code, string message) => Result.Failure<BankCategory>(new Failure(code, message, null));

    private static Result<BankCategory> NotFound(Guid id) => Result.Failure<BankCategory>(new NotFoundFailure("bank category", id.ToString()));

    internal static string Wire<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    private static CategoryKind Kind(string wire) => Enum.Parse<CategoryKind>(wire, ignoreCase: true);

    private static BankCategory ToModel(CategoryRow r) => new(r.Id, r.ParentId, r.Key, r.Name, Kind(r.Kind), r.ArchivedAt, r.MergedIntoId);
}
