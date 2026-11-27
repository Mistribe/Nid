using System.Globalization;
using Nid.Assets.Contracts;
using Nid.Assets.Domain;
using Nid.RealEstate.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
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
    IVisibleItems visible, IItemRegistry items, IAssetRecordStore assets, IValuations valuations, IHouseholdCalendar calendar, IActorAccessor actor,
    IHouseholdSettings household, IFxRates fx)
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
        var rows = page.Rows.Select(r => AssetViews.Row(candidates[r.ItemId], r, series.GetValueOrDefault(r.ItemId) ?? [], today)).ToList();
        return Result.Success(new AssetListView(rows, page.Total, page.Kinds, await GroupsAsync(rows, today, ct)));
    }

    // shortcut: groups gather the rows of the page answered, so a person's assets past the 50th split across pages; group in SQL if a household ever holds that many.
    private async Task<IReadOnlyList<AssetGroupView>> GroupsAsync(IReadOnlyList<AssetRow> rows, LocalDate today, CancellationToken ct)
    {
        var reference = await household.ReferenceCurrencyAsync(ct);
        var values = new Dictionary<ItemId, (decimal Amount, Currency Currency)>();
        foreach (var r in rows)
            if (r.Value is { } v && Currency.TryCreate(v.Currency, out var c))
                values[r.Id] = (decimal.Parse(v.Amount, CultureInfo.InvariantCulture), c);
        var foreign = values.Values.Select(v => v.Currency).Where(c => c != reference).Distinct().ToList();
        // Rates are per USD: the reference's own quote is needed too.
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync([.. foreign, reference], today, today, ct);
        return rows
            .GroupBy(r => string.Join(',', r.Owners.OrderBy(o => o.PersonId).Select(o => $"{o.PersonId}:{o.ShareBp}")))
            .Select(g =>
            {
                // An asset without a value adds nothing; one whose rate is missing, or none valued, leaves the total unknown.
                var amounts = g.Where(r => values.ContainsKey(r.Id)).Select(r => Convert(values[r.Id])).ToList();
                var total = amounts.Count == 0 || amounts.Any(a => a is null) ? null : (decimal?)amounts.Sum(a => a!.Value);
                return new AssetGroupView(g.First().Owners, g.Select(r => r.Id.Value).ToList(),
                    total is { } t ? new MoneyView(t.ToString("0.00", CultureInfo.InvariantCulture), reference.Code) : null);
            })
            .ToList();

        decimal? Convert((decimal Amount, Currency Currency) v)
            => v.Currency == reference ? v.Amount
            : rates.TryConvert(v.Amount, v.Currency, reference, today, projected: false, out var converted, out _) ? Math.Round(converted, 2, MidpointRounding.AwayFromZero)
            : null;
    }
}

[RequestHandler<GetAssetQuery, AssetView>]
public sealed class GetAssetHandler(
    IAssetRecordStore assets, IItemRegistry items, IValuations valuations, IHouseholdCalendar calendar, IAccessPolicy policy, IActorAccessor actor,
    ISubscriptionsOfItems subscriptions, IFlows flows, IItemLoans itemLoans)
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
        // #648: the subscriptions linked to it that the reader sees, named by the Subscriptions module.
        var linked = await subscriptions.ForAsync(actor.Current!.PersonId!.Value, VisibilityScope.Me, [query.ItemId], ct);
        var today = await calendar.TodayAsync(ct);
        // #188: the readable loans financing it, with their balances today.
        var loans = await itemLoans.BalancesAsync(actor.Current!.PersonId!.Value, VisibilityScope.Me, [query.ItemId], today, ct);
        var hasCosts = found.Asset.Details.Kind != AssetKinds.Vehicle
            && await flows.ListAsync(query.ItemId, AssetCheck.Everything, new HashSet<FlowKind> { FlowKind.Expense }, ct) is { } costs && costs.TryGetValue(out var held) && held!.Count > 0;
        IReadOnlyList<MileageReading> readings = found.Asset.Details.Kind == AssetKinds.Vehicle ? await assets.ReadingsAsync(query.ItemId, ct) : [];
        return Result.Success(AssetViews.Detail(found.Item, found.Asset, list!, today, canWrite, linked.GetValueOrDefault(query.ItemId) ?? [], hasCosts,
            loans.GetValueOrDefault(query.ItemId) ?? [], readings));
    }
}

[RequestHandler<AssetDeletionQuery, ItemDeletionPreview>]
public sealed class AssetDeletionHandler(IItemDeletions deletions) : IRequestHandler<AssetDeletionQuery, ItemDeletionPreview>
{
    public async ValueTask<Result<ItemDeletionPreview>> HandleAsync(AssetDeletionQuery query, CancellationToken ct = default)
        => await deletions.PreviewAsync(query.ItemId, "assets", "asset", ct);
}
