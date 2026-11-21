using Nid.Banking.Contracts;

namespace Nid.Banking.Domain;

/// <summary>#427: the budgets proposed to a household that has none, from each top-level expense category's Mois habituel.</summary>
public static class BudgetSuggestions
{
    /// <summary>How many of the biggest are ticked at first.</summary>
    public const int Preselected = 8;

    /// <summary><paramref name="Committed"/>: something is committed there this month, so following the commitments is offered instead of an amount.</summary>
    public sealed record Suggestion(Guid Category, string Name, decimal Average, decimal Proposed, bool Committed, bool Preselected);

    /// <summary>The Mois habituel rounded up to the next 10, a multiple of 10 staying as it is.</summary>
    public static decimal Proposed(decimal average) => Math.Ceiling(average / 10m) * 10m;

    /// <summary>
    /// The active top-level expense categories whose branch holds no budget (an archived child's is inert, as when one is set) and whose
    /// Mois habituel, theirs plus their children's (<paramref name="usual"/>, per category), is above zero; most spent first, then by name.
    /// </summary>
    public static IReadOnlyList<Suggestion> For(
        IReadOnlyCollection<BankCategory> categories, IReadOnlySet<Guid> budgeted, IReadOnlyDictionary<Guid, decimal> usual,
        IReadOnlySet<Guid> committed, IComparer<string> byName)
        => [.. categories
            .Where(top => top is { Parent: null, Kind: CategoryKind.Expense, ArchivedAt: null, MergedInto: null } && !budgeted.Contains(top.Id)
                && !categories.Any(c => c.Parent == top.Id && c.ArchivedAt is null && budgeted.Contains(c.Id)))
            .Select(top => (Top: top, Average: Math.Round(
                categories.Where(c => c.Id == top.Id || c.Parent == top.Id).Sum(c => usual.GetValueOrDefault(c.Id)), 2, MidpointRounding.AwayFromZero)))
            .Where(t => t.Average > 0m)
            .OrderByDescending(t => t.Average).ThenBy(t => t.Top.Name, byName)
            .Select((t, i) => new Suggestion(t.Top.Id, t.Top.Name, t.Average, Proposed(t.Average), committed.Contains(t.Top.Id), i < Preselected))];
}
