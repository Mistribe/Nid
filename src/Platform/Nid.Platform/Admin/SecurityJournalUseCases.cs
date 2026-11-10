using Nid.Core.Contracts;
using Nid.Platform.Audit;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Admin;

/// <summary>Administration › Journal de sécurité (administration-and-audit.md §2.2, spec §5.1).</summary>
public sealed record SecurityJournalQuery(string? Event, int? Page) : IQuery<AdminSecurityEventPage>, IAdminOnly;

public sealed record AdminSecurityEventPage(IReadOnlyList<SecurityEventView> Items, int Total, int Page)
{
    public const int PageSize = 50;
}

[RequestHandler<SecurityJournalQuery, AdminSecurityEventPage>]
public sealed class SecurityJournalHandler(IAuditLog log, IActorAccessor accessor, IVisibleItems visible, IItemRegistry items)
    : IRequestHandler<SecurityJournalQuery, AdminSecurityEventPage>
{
    public async ValueTask<Result<AdminSecurityEventPage>> HandleAsync(SecurityJournalQuery query, CancellationToken ct = default)
    {
        // An event outside the journal is filtered to nothing, not refused: the filter never widens what is shown.
        IReadOnlyCollection<string> events = query.Event is null
            ? SecurityEventNames.AdminJournal
            : SecurityEventNames.AdminJournal.Contains(query.Event) ? [query.Event] : [];
        var (page, skip) = ListPage.Of(query.Page, AdminSecurityEventPage.PageSize);
        var (records, total) = await log.SecurityEventsPageAsync(events, skip, AdminSecurityEventPage.PageSize, ct);
        var views = await WithItemsAsync(records, records.Select(SecurityEventViews.From).ToList(), ct);
        return Result.Success(new AdminSecurityEventPage(views, total, page));
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
