using Nid.Core.Contracts;
using Nid.Core.Domain;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Infrastructure.Persistence;

public sealed class AccessPolicy(IItemRegistry items, IPersonRegistry persons) : IAccessPolicy
{
    public async Task<Result> CheckAsync(PersonId actor, ItemId item, AccessKind kind, CancellationToken ct)
    {
        var access = await items.FindAsync(item, ct);
        if (access is null)
            return NotFound(item);

        var adults = await persons.AdultIdsAsync(ct);
        if (!AccessRules.Readers(access.Visibility, access.Owners, adults).Contains(actor))
            return NotFound(item); // existence is not disclosed

        if (kind == AccessKind.Read)
            return Result.Success();

        if (kind == AccessKind.Own)
            return AccessRules.CanCreate(actor, access.Owners)
                ? Result.Success()
                : Result.Failure(new UnauthorizedFailure("Only the item's owners change its sharing, its visibility or its owners.", null));

        return AccessRules.Writers(access.Visibility, access.ShareLevel, access.Owners, adults).Contains(actor)
            ? Result.Success()
            : Result.Failure(new UnauthorizedFailure("You can read this item but not change it.", null));
    }

    public async Task<IReadOnlySet<ItemId>> CheckManyAsync(PersonId actor, IReadOnlyCollection<ItemId> ids, AccessKind kind, CancellationToken ct)
    {
        if (ids.Count == 0)
            return new HashSet<ItemId>();

        var found = await items.FindManyAsync(ids, ct);
        var adults = await persons.AdultIdsAsync(ct);
        // CheckAsync's rule: a reader, and for a write a writer too; an unknown item is nobody's.
        return found.Values
            .Where(a => AccessRules.Readers(a.Visibility, a.Owners, adults).Contains(actor)
                && kind switch
                {
                    AccessKind.Read => true,
                    AccessKind.Own => AccessRules.CanCreate(actor, a.Owners),
                    _ => AccessRules.Writers(a.Visibility, a.ShareLevel, a.Owners, adults).Contains(actor),
                })
            .Select(a => a.Id)
            .ToHashSet();
    }

    public async Task<Result> CheckCreationAsync(PersonId actor, IReadOnlyList<OwnerShare> owners, CancellationToken ct)
    {
        // An owner nobody knows is treated as an adult, the conservative reading: the creation is then
        // allowed only if the actor is an owner too, and ItemRegistry still refuses it as core.owner_unknown.
        var kinds = (await persons.ListAsync(ct)).ToDictionary(p => p.Id, p => p.Kind);
        var resolved = owners
            .Select(o => new Owner(o.PersonId, kinds.GetValueOrDefault(o.PersonId, PersonKind.Adult), o.ShareBp))
            .ToList();

        return AccessRules.CanCreate(actor, resolved)
            ? Result.Success()
            : Result.Failure(new UnauthorizedFailure(
                "An item is created by one of its owners, unless every owner is a dependent.", null));
    }

    private static Result NotFound(ItemId item) => Result.Failure(new NotFoundFailure("item", item.ToString()));
}
