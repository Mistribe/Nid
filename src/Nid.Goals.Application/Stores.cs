using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Goals.Application;

public sealed record Goal(ItemId ItemId, string Kind, decimal? TargetAmount, int? Months, LocalDate? TargetDate, string? Note, uint Version);

/// <summary>An envelope funding a goal, as a share of the envelope in basis points (10 000 = all of it).</summary>
public sealed record Funding(ItemId Goal, ItemId Envelope, int ShareBp);

public sealed record GoalEntry(ItemId Goal, LocalDate On, decimal Amount);

/// <summary>A goal imported under a file id: its item, the entity's hash, and who imported it.</summary>
public sealed record ImportedGoal(ItemId Item, string Hash, PersonId ImportedBy);

public sealed record StoredGoal(Goal Goal, ImportMark? Import);

public interface IGoalStore
{
    Task CreateAsync(Goal goal, CancellationToken ct, ImportMark? import = null);

    Task<Goal?> FindAsync(ItemId id, CancellationToken ct);

    Task<IReadOnlyList<Goal>> FindManyAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct);

    /// <summary>
    /// Always writes the row, so every edit bumps the version. Fails with <see cref="Nid.Goals.Contracts.GoalsCodes.Conflict"/> when the goal
    /// changed since <see cref="Goal.Version"/> was read.
    /// </summary>
    Task<Result> UpdateAsync(Goal goal, CancellationToken ct);

    /// <summary>Import: the goals stored under these file ids, every importer's.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<ImportedGoal>>> FindImportedAsync(IReadOnlyCollection<string> refs, CancellationToken ct);

    /// <summary>Import: replaces the editable fields and the mark, without a version check.</summary>
    Task ReplaceAsync(Goal goal, ImportMark import, CancellationToken ct);

    /// <summary>Export: the goals among <paramref name="ids"/> with their import marks, in item id order.</summary>
    Task<IReadOnlyList<StoredGoal>> ReadAllAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct);

    Task<IReadOnlyList<Funding>> FundingOfAsync(IReadOnlyCollection<ItemId> goals, CancellationToken ct);

    Task<IReadOnlyList<Funding>> FundingOnAsync(IReadOnlyCollection<ItemId> envelopes, CancellationToken ct);

    Task ReplaceFundingAsync(ItemId goal, IReadOnlyList<Funding> funding, CancellationToken ct);

    Task<IReadOnlyList<GoalEntry>> EntriesOfAsync(IReadOnlyCollection<ItemId> goals, CancellationToken ct);

    /// <summary>Upsert on (goal, date).</summary>
    Task PutEntryAsync(GoalEntry entry, CancellationToken ct);

    Task<bool> DeleteEntryAsync(ItemId goal, LocalDate on, CancellationToken ct);
}
