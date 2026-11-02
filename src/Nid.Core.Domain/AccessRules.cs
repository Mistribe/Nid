using Nid.Core.Contracts;

namespace Nid.Core.Domain;

/// <summary>The only source of truth for who can read and write an item (<c>identity-and-access/authorization.md</c> §5).</summary>
public static class AccessRules
{
    public static IReadOnlySet<PersonId> Readers(
        Visibility visibility, IReadOnlyList<Owner> owners, IReadOnlyCollection<PersonId> adults)
        => visibility == Visibility.Private
            ? owners.Select(o => o.PersonId).ToHashSet()
            : adults.ToHashSet();

    /// <summary>
    /// Who may create an item (§5): one of its owners, or anyone when every owner is a <c>dependent</c> —
    /// a dependent has no login, so both adults create on their behalf. "On ne crée pas un élément au seul
    /// nom de l'autre adulte": an item with no owner at all is nobody's, and is refused here too.
    /// </summary>
    public static bool CanCreate(PersonId actor, IReadOnlyList<Owner> owners)
        => owners.Count > 0
           && (owners.Any(o => o.PersonId == actor) || owners.All(o => o.Kind == PersonKind.Dependent));

    /// <summary>
    /// 1F3b (user ruling 2026-09-28): a replacement does not take an item away from another adult. True when
    /// <paramref name="after"/> no longer names an adult owner of <paramref name="before"/> other than the actor
    /// (the actor may give up their own ownership).
    /// </summary>
    public static bool DropsAnotherAdultOwner(PersonId actor, IReadOnlyList<Owner> before, IReadOnlyList<Owner> after)
        => before.Any(o => o.Kind == PersonKind.Adult && o.PersonId != actor && after.All(a => a.PersonId != o.PersonId));

    /// <summary>
    /// 1F3b (user ruling 2026-09-28): true when an adult other than the actor reads the item as it is and would not
    /// read it as <paramref name="afterVisibility"/> and <paramref name="after"/> make it (by <see cref="Readers"/>).
    /// </summary>
    public static bool TakesReadAway(
        PersonId actor, Visibility beforeVisibility, IReadOnlyList<Owner> before,
        Visibility afterVisibility, IReadOnlyList<Owner> after, IReadOnlyCollection<PersonId> adults)
    {
        var still = Readers(afterVisibility, after, adults);
        return Readers(beforeVisibility, before, adults).Any(p => p != actor && adults.Contains(p) && !still.Contains(p));
    }

    /// <summary>
    /// #294: a shared item at the <see cref="ShareLevel.Edit"/> level is written by every adult; editing is not owning,
    /// so changing its level, visibility or owners stays with whoever <see cref="CanCreate"/> it.
    /// </summary>
    public static IReadOnlySet<PersonId> Writers(
        Visibility visibility, ShareLevel level, IReadOnlyList<Owner> owners, IReadOnlyCollection<PersonId> adults)
    {
        if (owners.Any(o => o.Kind == PersonKind.Dependent) || (visibility == Visibility.Shared && level == ShareLevel.Edit))
            return adults.ToHashSet();

        // Private items have exactly one owner, a single owner writes alone, several owners write together:
        // in every case the writers are the owners themselves.
        return owners.Select(o => o.PersonId).ToHashSet();
    }
}
