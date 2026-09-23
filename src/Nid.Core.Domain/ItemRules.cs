using Nid.Core.Contracts;
using UnambitiousFx.Functional;

namespace Nid.Core.Domain;

public sealed class ItemRules : IItemRules
{
    public Result ValidateOwners(Visibility visibility, IReadOnlyList<Owner> owners) => OwnershipRules.Validate(visibility, owners);

    public bool CanCreate(PersonId actor, IReadOnlyList<Owner> owners) => AccessRules.CanCreate(actor, owners);
}
