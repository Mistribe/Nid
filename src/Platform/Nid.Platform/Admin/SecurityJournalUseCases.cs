using Nid.Core.Contracts;
using Nid.Platform.Audit;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Admin;

/// <summary>Administration › Journal de sécurité (administration-and-audit.md §2.2, spec §5.1).</summary>
public sealed record SecurityJournalQuery(string? Event, long? Before, int? Limit) : IQuery<SecurityEventPage>, IAdminOnly;

[RequestHandler<SecurityJournalQuery, SecurityEventPage>]
public sealed class SecurityJournalHandler(IAuditLog log, IActorAccessor accessor, IVisibleItems visible, IItemRegistry items)
    : IRequestHandler<SecurityJournalQuery, SecurityEventPage>
{
    public async ValueTask<Result<SecurityEventPage>> HandleAsync(SecurityJournalQuery query, CancellationToken ct = default)
    {
        // An event outside the journal is filtered to nothing, not refused: the filter never widens what is shown.
        IReadOnlyCollection<string> events = query.Event is null
            ? SecurityEventNames.AdminJournal
            : SecurityEventNames.AdminJournal.Contains(query.Event) ? [query.Event] : [];
        var limit = AuditPaging.Limit(query.Limit);
        var records = await log.SecurityEventsAsync(null, events, withoutSelfRevocations: true, query.Before, limit, ct);
        var page = SecurityEventViews.Page(records, limit);
        return Result.Success(page with { Entries = await WithItemsAsync(records, page.Entries, ct) });
    }

    /// <summary>Only an item the reading admin may see is named: the others stay an id, in one lookup for the page (#28).</summary>
    private async Task<IReadOnlyList<SecurityEventView>> WithItemsAsync(
        IReadOnlyList<AuditRecord> records, IReadOnlyList<SecurityEventView> views, CancellationToken ct)
    {
        var outside = views.Select(v => v.Details.GetValueOrDefault("reason") == AccessViolationReasons.OutsideVisibleSet).ToList();
        var ids = records.Where((_, i) => outside[i]).SelectMany(r => r.ItemIds).Select(id => new ItemId(id)).ToHashSet();
        if (ids.Count == 0 || accessor.Current?.PersonId is not { } admin)
            return views;

        ids.IntersectWith(await visible.ForAsync(admin, VisibilityScope.Me, includeArchived: true, ct));
        var named = await items.FindManyAsync(ids, ct);
        return views.Select((view, i) => outside[i]
            ? view with
            {
                Items = records[i].ItemIds
                    .Select(id => named.GetValueOrDefault(new ItemId(id)) is { } item
                        ? new SecurityEventItem(id, item.Kind, item.DisplayName)
                        : new SecurityEventItem(id, null, null))
                    .ToList(),
            }
            : view).ToList();
    }
}
