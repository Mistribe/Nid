using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;

namespace Nid.Core.Infrastructure.Persistence;

/// <summary>#156: Core's rows that name a person: item ownerships and projection scenarios (an event's target or date).</summary>
public sealed class CorePersonUsePart(CoreDbContext db) : IPersonUsePart
{
    public async Task<IReadOnlyList<DeletionCount>> UsesAsync(PersonId person, CancellationToken ct)
    {
        var id = person.Value;
        // A scenario keeps its persons in its JSON document: any mention of the id, whatever the scenario's visibility.
        var mention = $"%{id}%";
        return
        [
            new("ownerships", await db.Ownerships.CountAsync(o => o.PersonId == id, ct)),
            new("scenarios", await db.Scenarios.FromSql($"select * from core.scenario where data::text like {mention}").CountAsync(ct)),
        ];
    }
}
