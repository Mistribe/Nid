using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Subscriptions.Application.Catalog;

// Spec §6 (amended after 1G): the sync journal and the sync are the admin's (IAdminOnly, 403 platform.admin_required
// to anyone else). None of the three touches an item, as AdminMarkerTests demands of every admin-only request.

/// <summary>Runs one sync now, in the caller's request: what the <c>catalog-sync</c> job sends as the system actor (an admin).</summary>
public sealed record SyncCatalogCommand : ICommand<CatalogSyncRunView>, IAdminOnly;

/// <summary>Administration › Catalogue's "Synchroniser maintenant": triggers the job, or 409 while a sync runs.</summary>
public sealed record RequestCatalogSyncCommand : ICommand<CatalogSyncRequested>, IAdminOnly;

public sealed record GetCatalogRunsQuery(int? Page) : IQuery<CatalogRunsView>, IAdminOnly;

[RequestHandler<SyncCatalogCommand, CatalogSyncRunView>]
public sealed class SyncCatalogHandler(CatalogSync sync, IVisibleItems visible, IActorAccessor actor)
    : IRequestHandler<SyncCatalogCommand, CatalogSyncRunView>
{
    public async ValueTask<Result<CatalogSyncRunView>> HandleAsync(SyncCatalogCommand command, CancellationToken ct = default)
    {
        var ran = await sync.RunAsync(ct);
        if (ran.TryGetFailure(out var failure))
            return Result.Failure<CatalogSyncRunView>(failure!);
        ran.TryGetValue(out var run);

        // The caller sees the report as the journal would show it to them (spec §3.6); the job, a system actor, all of it.
        var filter = await CatalogReportFilter.ForAsync(actor.Current, [run!], visible, ct);
        return Result.Success(CatalogSyncRunView.From(run!, filter));
    }
}

[RequestHandler<RequestCatalogSyncCommand, CatalogSyncRequested>]
public sealed class RequestCatalogSyncHandler(ICatalogRunLog runs, ICatalogJobTrigger trigger)
    : IRequestHandler<RequestCatalogSyncCommand, CatalogSyncRequested>
{
    public async ValueTask<Result<CatalogSyncRequested>> HandleAsync(RequestCatalogSyncCommand command, CancellationToken ct = default)
    {
        if (await runs.IsRunningAsync(ct))
            return Result.Failure<CatalogSyncRequested>(new Failure(CatalogCodes.SyncRunning, "A catalog sync is already running.", null));

        var triggered = await trigger.TriggerSyncAsync(ct);
        return triggered.TryGetFailure(out var failure)
            ? Result.Failure<CatalogSyncRequested>(failure!)
            : Result.Success(new CatalogSyncRequested(true));
    }
}

[RequestHandler<GetCatalogRunsQuery, CatalogRunsView>]
public sealed class GetCatalogRunsHandler(
    ICatalogRunLog runs, CatalogOptions options, IVisibleItems visible, IActorAccessor actor)
    : IRequestHandler<GetCatalogRunsQuery, CatalogRunsView>
{
    public async ValueTask<Result<CatalogRunsView>> HandleAsync(GetCatalogRunsQuery query, CancellationToken ct = default)
    {
        var (page, skip) = ListPage.Of(query.Page, CatalogRunPage.PageSize);
        var (items, total) = await runs.PageAsync(skip, CatalogRunPage.PageSize, ct);
        var latest = await runs.RecentAsync(1, ct);
        var filter = await CatalogReportFilter.ForAsync(actor.Current, [.. latest, .. items], visible, ct);
        return Result.Success(new CatalogRunsView(options.Commit, latest.Select(r => CatalogSyncRunView.From(r, filter)).SingleOrDefault(),
            new CatalogRunPage(items.Select(r => CatalogSyncRunView.From(r, filter)).ToList(), total, page)));
    }
}
