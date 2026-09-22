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
