using Nid.Core.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Domain;

/// <summary>Invariants of an item's owners (<c>core-data-model.md</c> §4.2).</summary>
public static class OwnershipRules
{
    public const int FullShareBp = 10_000;

    public static Result Validate(Visibility visibility, IReadOnlyList<Owner> owners)
    {
        if (owners.Count == 0)
            return Fail("core.owner_required", "An item has at least one owner.");

        if (owners.Select(o => o.PersonId).Distinct().Count() != owners.Count)
            return Fail("core.owner_duplicate", "A person appears only once among the owners.");

        if (owners.Any(o => o.ShareBp is < 1 or > FullShareBp))
            return Fail("core.share_range", "Each share is between 1 and 10000 basis points.");

        if (owners.Sum(o => o.ShareBp) != FullShareBp)
            return Fail("core.share_sum", "Shares add up to exactly 10000 basis points.");

        if (visibility == Visibility.Private)
        {
            if (owners.Count != 1)
                return Fail("core.private_single_owner", "A private item has exactly one owner.");
            if (owners[0].Kind == PersonKind.Dependent)
                return Fail("core.private_dependent", "A dependent cannot own a private item: both adults would be excluded.");
        }

        return Result.Success();
    }

    private static Result Fail(string code, string message) => Result.Failure(new Failure(code, message, null));
}
