using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Goals.Application;
using Nid.Goals.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Goals.Infrastructure.Persistence;

public sealed class GoalStore(GoalsDbContext db) : IGoalStore
{
    public async Task CreateAsync(Goal goal, CancellationToken ct, ImportMark? import = null)
    {
        var row = new GoalRow { ItemId = goal.ItemId.Value };
        Apply(row, goal);
        Mark(row, import);
        db.Goals.Add(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Goal?> FindAsync(ItemId id, CancellationToken ct)
        => await db.Goals.AsNoTracking().SingleOrDefaultAsync(g => g.ItemId == id.Value, ct) is { } row ? ToGoal(row) : null;

    public async Task<IReadOnlyList<Goal>> FindManyAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct)
    {
        var wanted = ids.Select(i => i.Value).ToList();
        return (await db.Goals.AsNoTracking().Where(g => wanted.Contains(g.ItemId)).ToListAsync(ct)).Select(ToGoal).ToList();
    }

    public async Task<Result> UpdateAsync(Goal goal, CancellationToken ct)
    {
        var row = await db.Goals.SingleOrDefaultAsync(g => g.ItemId == goal.ItemId.Value, ct);
        if (row is null)
            return Result.Failure(new NotFoundFailure("goal", goal.ItemId.ToString()));

        Apply(row, goal);
        db.Entry(row).Property(r => r.Version).OriginalValue = goal.Version;
        // Always written: an edit of the name or the funding alone still bumps xmin, so a later stale edit meets the conflict.
        db.Entry(row).Property(r => r.Kind).IsModified = true;
        try
        {
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure(new Failure(GoalsCodes.Conflict, "The goal changed since it was read.", null));
        }
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ImportedGoal>>> FindImportedAsync(
        IReadOnlyCollection<string> refs, CancellationToken ct)
    {
        var wanted = refs.Distinct(StringComparer.Ordinal).ToList();
        var rows = await db.Goals.AsNoTracking().Where(r => r.ImportRef != null && wanted.Contains(r.ImportRef))
            .Select(r => new { r.ItemId, r.ImportRef, r.ImportHash, r.ImportedBy }).ToListAsync(ct);
        return rows.GroupBy(r => r.ImportRef!, StringComparer.Ordinal).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<ImportedGoal>)g.Select(r => new ImportedGoal(new ItemId(r.ItemId), r.ImportHash!.Trim(), new PersonId(r.ImportedBy!.Value))).ToList(),
            StringComparer.Ordinal);
    }

    public async Task ReplaceAsync(Goal goal, ImportMark import, CancellationToken ct)
    {
        var row = await db.Goals.SingleAsync(g => g.ItemId == goal.ItemId.Value, ct);
        Apply(row, goal);
        Mark(row, import);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<StoredGoal>> ReadAllAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct)
    {
        var wanted = ids.Select(i => i.Value).ToList();
        var rows = await db.Goals.AsNoTracking().Where(g => wanted.Contains(g.ItemId)).OrderBy(g => g.ItemId).ToListAsync(ct);
        return rows.Select(r => new StoredGoal(ToGoal(r),
            r.ImportRef is null ? null : new ImportMark(r.ImportRef, r.ImportHash!.Trim(), new PersonId(r.ImportedBy!.Value)))).ToList();
    }

    public async Task<IReadOnlyList<Funding>> FundingOfAsync(IReadOnlyCollection<ItemId> goals, CancellationToken ct)
    {
        var ids = goals.Select(g => g.Value).ToList();
        var rows = await db.Funding.AsNoTracking().Where(f => ids.Contains(f.GoalId)).OrderBy(f => f.GoalId).ThenBy(f => f.EnvelopeId).ToListAsync(ct);
        return rows.Select(ToFunding).ToList();
    }

    public async Task<IReadOnlyList<Funding>> FundingOnAsync(IReadOnlyCollection<ItemId> envelopes, CancellationToken ct)
    {
        var ids = envelopes.Select(e => e.Value).ToList();
        var rows = await db.Funding.AsNoTracking().Where(f => ids.Contains(f.EnvelopeId)).OrderBy(f => f.GoalId).ThenBy(f => f.EnvelopeId).ToListAsync(ct);
        return rows.Select(ToFunding).ToList();
    }

    public async Task ReplaceFundingAsync(ItemId goal, IReadOnlyList<Funding> funding, CancellationToken ct)
    {
        db.Funding.RemoveRange(await db.Funding.Where(f => f.GoalId == goal.Value).ToListAsync(ct));
        db.Funding.AddRange(funding.Select(f => new FundingRow { GoalId = goal.Value, EnvelopeId = f.Envelope.Value, ShareBp = f.ShareBp }));
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<GoalEntry>> EntriesOfAsync(IReadOnlyCollection<ItemId> goals, CancellationToken ct)
    {
        var ids = goals.Select(g => g.Value).ToList();
        var rows = await db.Entries.AsNoTracking().Where(e => ids.Contains(e.GoalId)).OrderBy(e => e.GoalId).ThenBy(e => e.On).ToListAsync(ct);
        return rows.Select(r => new GoalEntry(new ItemId(r.GoalId), r.On, r.Amount)).ToList();
    }

    public async Task PutEntryAsync(GoalEntry entry, CancellationToken ct)
    {
        var row = await db.Entries.SingleOrDefaultAsync(e => e.GoalId == entry.Goal.Value && e.On == entry.On, ct);
        if (row is null)
            db.Entries.Add(new ProgressEntryRow { GoalId = entry.Goal.Value, On = entry.On, Amount = entry.Amount });
        else
            row.Amount = entry.Amount;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteEntryAsync(ItemId goal, LocalDate on, CancellationToken ct)
    {
        var row = await db.Entries.SingleOrDefaultAsync(e => e.GoalId == goal.Value && e.On == on, ct);
        if (row is null)
            return false;

        db.Entries.Remove(row); // tracked, so the audit interceptor sees it
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static void Mark(GoalRow row, ImportMark? import)
    {
        row.ImportRef = import?.Ref;
        row.ImportHash = import?.Hash;
        row.ImportedBy = import?.ImportedBy.Value;
    }

    private static void Apply(GoalRow row, Goal goal)
    {
        row.Kind = goal.Kind;
        row.TargetAmount = goal.TargetAmount;
        row.Months = goal.Months;
        row.TargetDate = goal.TargetDate;
        row.Note = goal.Note;
    }

    private static Goal ToGoal(GoalRow r) => new(new ItemId(r.ItemId), r.Kind, r.TargetAmount, r.Months, r.TargetDate, r.Note, r.Version);

    private static Funding ToFunding(FundingRow r) => new(new ItemId(r.GoalId), new ItemId(r.EnvelopeId), r.ShareBp);
}
