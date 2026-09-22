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

    public static IReadOnlySet<PersonId> Writers(
        Visibility visibility, IReadOnlyList<Owner> owners, IReadOnlyCollection<PersonId> adults)
    {
        if (owners.Any(o => o.Kind == PersonKind.Dependent))
            return adults.ToHashSet();

        // Private items have exactly one owner, a single owner writes alone, several owners write together:
        // in every case the writers are the owners themselves.
        return owners.Select(o => o.PersonId).ToHashSet();
    }
}
