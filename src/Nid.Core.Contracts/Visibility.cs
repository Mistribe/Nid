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
    Task<IReadOnlySet<ItemId>> ForAsync(PersonId actor, VisibilityScope scope, CancellationToken ct);
}

/// <summary>
/// A response that carries item-scoped data. The output guard reads this to check nothing outside the
/// actor's visible set escapes; a response that exposes items without implementing it would bypass the guard.
/// </summary>
public interface IExposesItems
{
    IEnumerable<ItemId> ExposedItems();
}
