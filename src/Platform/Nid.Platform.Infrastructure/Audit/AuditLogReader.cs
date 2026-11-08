using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Platform.Audit;
using Nid.Platform.Contracts;
using Nid.Platform.Infrastructure.Persistence;

namespace Nid.Platform.Infrastructure.Audit;

public sealed class AuditLogReader(PlatformDbContext db) : IAuditLog
{
    public async Task<IReadOnlyList<AuditRecord>> ItemHistoryAsync(
        ItemId item, IReadOnlySet<ItemId> visible, long? before, int limit, CancellationToken ct)
    {
        var target = new[] { item.Value };
        var allowed = visible.Select(v => v.Value).ToArray();
        var rows = await db.AuditLog.FromSql($"""
                select * from platform.audit_log
                where category = 'write' and item_ids @> {target} and item_ids <@ {allowed}
                  and ({before}::bigint is null or seq < {before})
                order by seq desc limit {limit}
                """)
            .AsNoTracking().ToListAsync(ct);
        return rows.Select(ToRecord).ToList();
    }

    public async Task<IReadOnlyList<AuditRecord>> SecurityEventsAsync(
        PersonId? subject, IReadOnlyCollection<string>? events, bool withoutSelfRevocations, long? before, int limit, CancellationToken ct)
    {
        var query = db.AuditLog.AsNoTracking().Where(r => r.Category == "security");
        if (subject is { } person)
            query = query.Where(r => r.SubjectPersonId == person.Value);
        if (events is not null)
        {
            var names = events.ToArray();
            query = query.Where(r => names.Contains(r.Action));
        }
        if (withoutSelfRevocations)
            query = query.Where(r => !(r.Action == SecurityEventNames.SessionRevoked && r.PersonId == r.SubjectPersonId));
        if (before is { } cursor)
            query = query.Where(r => r.Seq < cursor);

        return (await query.OrderByDescending(r => r.Seq).Take(limit).ToListAsync(ct)).Select(ToRecord).ToList();
    }

    private static AuditRecord ToRecord(AuditEntryRow r) => new(
        r.Seq, r.At, r.Category, r.Action, r.ActorKind, r.PersonId, r.Via, r.Request, r.Entity, r.EntityKey,
        r.ItemIds, r.SubjectPersonId, r.Data, r.TokenId);
}
