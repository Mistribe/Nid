namespace Nid.Core.Contracts;

/// <summary>What a read is scoped to, matching the Household / Me selector of <c>frontend/screens.md</c> §13.</summary>
public enum VisibilityScope
{
    /// <summary>Shared items only.</summary>
    Household,

    /// <summary>Shared items plus the actor's own private ones.</summary>
    Me,
}

/// <summary>
/// The single read filter every module reuses (<c>core-data-model.md</c> §355). Visibility is enforced by
/// application filtering, not by Row-Level Security (<c>architecture.md</c> D15).
/// </summary>
public interface IVisibleItems
{
    /// <summary>Active items only: shared items, plus — in the <c>Me</c> scope — the private items the actor owns.</summary>
    Task<IReadOnlySet<ItemId>> ForAsync(PersonId actor, VisibilityScope scope, CancellationToken ct)
        => ForAsync(actor, scope, includeArchived: false, ct);

    /// <summary>
    /// Returns the actor's visible items, optionally including archived ones.
    /// Archived items stay readable by whoever could read them; <c>includeArchived: true</c> is for checks
    /// of what may be exposed (output guard, item history) and for lists that ask for closed items.
    /// </summary>
    Task<IReadOnlySet<ItemId>> ForAsync(PersonId actor, VisibilityScope scope, bool includeArchived, CancellationToken ct);
}

/// <summary>
/// A response that carries item-scoped data. The output guard reads this to check nothing outside the
/// actor's visible set escapes; a response that exposes items without implementing it would bypass the guard.
/// </summary>
public interface IExposesItems
{
    IEnumerable<ItemId> ExposedItems();
}
