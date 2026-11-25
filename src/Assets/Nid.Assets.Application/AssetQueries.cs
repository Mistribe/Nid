using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Assets.Application;

/// <summary>The list's question, all of it answered in SQL: <paramref name="Page"/> from 1.</summary>
public sealed record AssetFilter(string? Kind, PersonId? Owner, bool Archived, string? Search, int Page);

public sealed record ListAssetsQuery(VisibilityScope Scope, AssetFilter Filter) : IQuery<AssetListView>;

public sealed record GetAssetQuery(ItemId ItemId) : IQuery<AssetView>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

/// <summary>What deleting an asset removes, read before the delete.</summary>
public sealed record AssetDeletionQuery(ItemId ItemId) : IQuery<ItemDeletionPreview>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

[RequestHandler<ListAssetsQuery, AssetListView>]
public sealed class ListAssetsHandler(
    IVisibleItems visible, IItemRegistry items, IAssetRecordStore assets, IValuations valuations, IHouseholdCalendar calendar, IActorAccessor actor)
    : IRequestHandler<ListAssetsQuery, AssetListView>
{
    public const int PageSize = 50;
    public const int MaxPage = 100_000;

    public async ValueTask<Result<AssetListView>> HandleAsync(ListAssetsQuery query, CancellationToken ct = default)
    {
        var filter = query.Filter;
        if (filter.Page is < 1 or > MaxPage)
            return Result.Failure<AssetListView>(AssetCheck.Shape("page", $"A page is 1 to {MaxPage}."));
        var ids = await visible.ForAsync(actor.Current!.PersonId!.Value, query.Scope, includeArchived: filter.Archived, ct);
        var search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim();
        var candidates = (await items.SearchAsync(ids.ToList(), "assets", "asset", search, ct))
            .Where(i => (i.ArchivedAt is not null) == filter.Archived && (filter.Owner is not { } owner || i.Owners.Any(o => o.PersonId == owner)))
            .ToDictionary(i => i.Id);

        var page = await assets.ListAsync(candidates.Keys.ToList(), filter.Kind, (filter.Page - 1) * PageSize, PageSize, ct);
        var series = page.Rows.Count == 0
            ? new Dictionary<ItemId, IReadOnlyList<Valuation>>()
            : await valuations.SeriesManyAsync(page.Rows.Select(r => r.ItemId).ToList(), AssetCheck.Everything, ct) is { } read && read.TryGetValue(out var map)
                ? map!.ToDictionary(e => e.Key, e => e.Value)
                : throw new InvalidOperationException("The assets' valuations could not be read.");
        var today = await calendar.TodayAsync(ct);
        return Result.Success(new AssetListView(
            page.Rows.Select(r => AssetViews.Row(candidates[r.ItemId], r, series.GetValueOrDefault(r.ItemId) ?? [], today)).ToList(),
            page.Total, page.Kinds));
    }
}

[RequestHandler<GetAssetQuery, AssetView>]
public sealed class GetAssetHandler(
    IAssetRecordStore assets, IItemRegistry items, IValuations valuations, IHouseholdCalendar calendar, IAccessPolicy policy, IActorAccessor actor)
    : IRequestHandler<GetAssetQuery, AssetView>
{
    public async ValueTask<Result<AssetView>> HandleAsync(GetAssetQuery query, CancellationToken ct = default)
    {
        var loaded = await AssetCheck.LoadAsync(assets, items, query.ItemId, ct);
        if (!loaded.TryGetValue(out var found))
            return Result.Failure<AssetView>(loaded.TryGetFailure(out var missing) ? missing! : throw new InvalidOperationException());
        var series = await valuations.SeriesAsync(query.ItemId, AssetCheck.Everything, ct);
        if (!series.TryGetValue(out var list))
            return Result.Failure<AssetView>(series.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
        var canWrite = (await policy.CheckAsync(actor.Current!.PersonId!.Value, query.ItemId, AccessKind.Write, ct)).IsSuccess;
        return Result.Success(AssetViews.Detail(found.Item, found.Asset, list!, await calendar.TodayAsync(ct), canWrite));
    }
}

[RequestHandler<AssetDeletionQuery, ItemDeletionPreview>]
public sealed class AssetDeletionHandler(IItemDeletions deletions) : IRequestHandler<AssetDeletionQuery, ItemDeletionPreview>
{
    public async ValueTask<Result<ItemDeletionPreview>> HandleAsync(AssetDeletionQuery query, CancellationToken ct = default)
        => await deletions.PreviewAsync(query.ItemId, "assets", "asset", ct);
}
