using Nid.Platform.Audit;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Admin;

/// <summary>Administration › Journal de sécurité (administration-and-audit.md §2.2, spec §5.1).</summary>
public sealed record SecurityJournalQuery(string? Event, long? Before, int? Limit) : IQuery<SecurityEventPage>, IAdminOnly;

[RequestHandler<SecurityJournalQuery, SecurityEventPage>]
public sealed class SecurityJournalHandler(IAuditLog log) : IRequestHandler<SecurityJournalQuery, SecurityEventPage>
{
    public async ValueTask<Result<SecurityEventPage>> HandleAsync(SecurityJournalQuery query, CancellationToken ct = default)
    {
        // An event outside the journal is filtered to nothing, not refused: the filter never widens what is shown.
        IReadOnlyCollection<string> events = query.Event is null
            ? SecurityEventNames.AdminJournal
            : SecurityEventNames.AdminJournal.Contains(query.Event) ? [query.Event] : [];
        var limit = AuditPaging.Limit(query.Limit);
        return Result.Success(SecurityEventViews.Page(
            await log.SecurityEventsAsync(null, events, withoutSelfRevocations: true, query.Before, limit, ct), limit));
    }
}
