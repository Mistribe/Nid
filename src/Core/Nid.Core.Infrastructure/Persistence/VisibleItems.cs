using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;

namespace Nid.Core.Infrastructure.Persistence;

/// <summary>
/// One query: shared items, plus — in the <c>Me</c> and <c>Mine</c> scopes — the private items the actor owns; <c>Mine</c> leaves out
/// the items only dependents own (#360). A private item of the other adult is unreachable in every scope, which is the invariant the
/// leak bench proves. Off the premises (<c>Actor.Offsite</c>, #533), an item one of whose owners chose <c>never</c> is left out.
/// </summary>
public sealed class VisibleItems(CoreDbContext db, IActorAccessor? actors = null) : IVisibleItems
{
    public Task<IReadOnlySet<ItemId>> ForAsync(PersonId actor, VisibilityScope scope, CancellationToken ct)
        => ForAsync(actor, scope, includeArchived: false, ct);

    Task<IReadOnlySet<ItemId>> IVisibleItems.ForAsync(PersonId actor, VisibilityScope scope, CancellationToken ct)
        => ForAsync(actor, scope, includeArchived: false, ct);

    public async Task<IReadOnlySet<ItemId>> ForAsync(PersonId actor, VisibilityScope scope, bool includeArchived, CancellationToken ct)
    {
        var shared = VisibilityName(Visibility.Shared);
        var query = db.Items.AsNoTracking();

        if (!includeArchived)
            query = query.Where(i => i.ArchivedAt == null);

        query = scope == VisibilityScope.Household
            ? query.Where(i => i.Visibility == shared)
            : query.Where(i => i.Visibility == shared || i.Owners.Any(o => o.PersonId == actor.Value));
        if (actors?.Current is { Offsite: { Never.Count: > 0 } offsite, PersonId: { } reader } && reader == actor)
        {
            var never = offsite.Never.Select(p => p.Value).ToArray();
            query = query.Where(i => !i.Owners.Any(o => never.Contains(o.PersonId)));
        }
        if (scope == VisibilityScope.Mine)
            query = query.Where(i => i.Owners.Any(o => db.Persons.Any(p => p.Id == o.PersonId && p.Kind == "adult")));

        var ids = await query.Select(i => i.Id).ToListAsync(ct);
        return ids.Select(id => new ItemId(id)).ToHashSet();
    }

    internal static string VisibilityName(Visibility visibility) => visibility.ToString().ToLowerInvariant();
}
