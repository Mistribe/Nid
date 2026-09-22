using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;

namespace Nid.Core.Infrastructure.Persistence;

/// <summary>
/// One query: shared items, plus — in the <c>Me</c> scope — the private items the actor owns. A private item
/// of the other adult is unreachable in either scope, which is the invariant the leak bench proves.
/// </summary>
public sealed class VisibleItems(CoreDbContext db) : IVisibleItems
{
    public async Task<IReadOnlySet<ItemId>> ForAsync(PersonId actor, VisibilityScope scope, CancellationToken ct)
    {
        var shared = VisibilityName(Visibility.Shared);
        var query = db.Items.AsNoTracking().Where(i => i.ArchivedAt == null);

        query = scope == VisibilityScope.Household
            ? query.Where(i => i.Visibility == shared)
            : query.Where(i => i.Visibility == shared || i.Owners.Any(o => o.PersonId == actor.Value));

        var ids = await query.Select(i => i.Id).ToListAsync(ct);
        return ids.Select(id => new ItemId(id)).ToHashSet();
    }

    internal static string VisibilityName(Visibility visibility) => visibility.ToString().ToLowerInvariant();
}
