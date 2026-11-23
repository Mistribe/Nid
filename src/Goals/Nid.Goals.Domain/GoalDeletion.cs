using Nid.Core.Contracts;

namespace Nid.Goals.Domain;

/// <summary>
/// #23: what deleting an item means for Goals. A goal takes its funding links and progress entries with it; an envelope is blocked by
/// the open goals it funds (their funding is to be edited first), while the funding lines of the closed ones go with it (a closed goal
/// cannot be edited).
/// </summary>
public static class GoalDeletion
{
    public const string GoalModule = "goals";

    /// <summary>The registry's module and kind of an envelope (Investments), the only item a goal is funded by.</summary>
    public const string EnvelopeModule = "investments";
    public const string EnvelopeKind = "envelope";

    public static bool IsGoal(ItemAccess item) => item.Module == GoalModule;

    public static bool IsEnvelope(ItemAccess item) => item.Module == EnvelopeModule && item.Kind == EnvelopeKind;

    /// <summary>The goals an envelope funds that block its delete: the open ones.</summary>
    public static IReadOnlyList<ItemAccess> Blocking(IEnumerable<ItemAccess> funded) => funded.Where(g => g.ArchivedAt is null).ToList();

    /// <summary>The goals whose funding lines go with the envelope: the closed ones.</summary>
    public static IReadOnlyList<ItemAccess> Released(IEnumerable<ItemAccess> funded) => funded.Where(g => g.ArchivedAt is not null).ToList();
}
