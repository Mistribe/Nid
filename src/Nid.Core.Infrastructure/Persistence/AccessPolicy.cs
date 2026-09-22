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

        return AccessRules.Writers(access.Visibility, access.Owners, adults).Contains(actor)
            ? Result.Success()
            : Result.Failure(new UnauthorizedFailure("You can read this item but not change it.", null));
    }

    private static Result NotFound(ItemId item) => Result.Failure(new NotFoundFailure("item", item.ToString()));
}
