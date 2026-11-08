using Nid.Core.Contracts;
using UnambitiousFx.Functional;

namespace Nid.Core.Domain;

public sealed class ItemRules : IItemRules
{
    public Result ValidateOwners(Visibility visibility, IReadOnlyList<Owner> owners) => OwnershipRules.Validate(visibility, owners);

    public bool CanCreate(PersonId actor, IReadOnlyList<Owner> owners) => AccessRules.CanCreate(actor, owners);

    public bool DropsAnotherAdultOwner(PersonId actor, IReadOnlyList<Owner> before, IReadOnlyList<Owner> after)
        => AccessRules.DropsAnotherAdultOwner(actor, before, after);

    public bool TakesReadAway(PersonId actor, Visibility beforeVisibility, IReadOnlyList<Owner> before,
        Visibility afterVisibility, IReadOnlyList<Owner> after, IReadOnlyCollection<PersonId> adults)
        => AccessRules.TakesReadAway(actor, beforeVisibility, before, afterVisibility, after, adults);
}
