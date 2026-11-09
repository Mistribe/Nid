using Nid.Core.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Platform.Contracts;

/// <summary>
/// #25: an item's sharing and owners changed by its edit, under the import's guards (<see cref="ImportSectionKit.ReplaceRefusalAsync"/>):
/// editing is not owning (#294), and an edit never takes the item from another adult nor hides it from one who reads it today.
/// </summary>
public static class OwnershipEdit
{
    /// <summary>What the edit leaves: each part it does not send stays the item's. <paramref name="Changed"/>: any of them differs.</summary>
    public sealed record Target(Visibility Visibility, ShareLevel ShareLevel, IReadOnlyList<OwnerShare> Owners, bool Changed);

    public static Target Of(ItemAccess item, Visibility? visibility, ShareLevel? level, IReadOnlyList<OwnerShare>? owners)
    {
        var current = item.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList();
        var v = visibility ?? item.Visibility;
        var l = v == Visibility.Private ? ShareLevel.View : level ?? item.ShareLevel;
        var o = owners ?? current;
        return new Target(v, l, o, v != item.Visibility || l != item.ShareLevel || !current.ToHashSet().SetEquals(o));
    }

    /// <summary>The first refusal, or null; nothing to refuse when the edit changes neither. <paramref name="noun"/>: « envelope ».</summary>
    public static async Task<Failure?> RefusalAsync(
        IItemRules rules, IPersonRegistry persons, PersonId actor, ItemAccess item, Target target, string noun, CancellationToken ct)
    {
        if (!target.Changed)
            return null;
        if (!rules.CanCreate(actor, item.Owners))
            return new UnauthorizedFailure($"Only the {noun}'s owners change its sharing or its owners.", null);
        // Both rules read only the new owners' ids: their kind does not matter here.
        var after = target.Owners.Select(o => new Owner(o.PersonId, PersonKind.Adult, o.ShareBp)).ToList();
        if (rules.DropsAnotherAdultOwner(actor, item.Owners, after))
            return FieldErrors.At(new Failure(ItemCodes.DropsAdultOwner, "Another adult cannot be removed from the owners.", null), "owners");
        if (rules.TakesReadAway(actor, item.Visibility, item.Owners, target.Visibility, after, await persons.AdultIdsAsync(ct)))
            return FieldErrors.At(new Failure(ItemCodes.TakesReadAway, $"This hides the {noun} from another adult who sees it today.", null), "owners");
        return null;
    }

    /// <summary>Writes the name and the target in one replacement, when either changed; a refused sharing or owners is the owners field's.</summary>
    public static async Task<Result> ReplaceAsync(IItemRegistry items, ItemAccess item, string name, Target target, CancellationToken ct)
    {
        if (!target.Changed && name == item.DisplayName)
            return Result.Success();
        var replaced = await items.ReplaceAsync(item.Id, name, target.Visibility, target.Owners, ct, target.ShareLevel);
        return replaced.TryGetFailure(out var failure) && target.Changed ? Result.Failure(FieldErrors.At(failure!, "owners")) : replaced;
    }
}
