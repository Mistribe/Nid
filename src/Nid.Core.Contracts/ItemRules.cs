using UnambitiousFx.Functional;

namespace Nid.Core.Contracts;

/// <summary>
/// The Core's item rules, for a caller that must check an item before it exists — an import validating a
/// whole file writes nothing, so neither <see cref="IItemRegistry"/> nor <see cref="IAccessPolicy"/> can
/// look anything up for it.
/// </summary>
public interface IItemRules
{
    /// <summary><c>core-data-model.md</c> §4.2: shares, private single owner.</summary>
    Result ValidateOwners(Visibility visibility, IReadOnlyList<Owner> owners);

    /// <summary><c>authorization.md</c> §5: the actor is an owner, or every owner is a dependent.</summary>
    bool CanCreate(PersonId actor, IReadOnlyList<Owner> owners);
}
