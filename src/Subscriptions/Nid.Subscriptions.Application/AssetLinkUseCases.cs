using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Subscriptions.Application;

/// <summary>#648: links a subscription to an asset, or unlinks it (<paramref name="AssetId"/> null).</summary>
public sealed record SetSubscriptionAssetCommand(ItemId ItemId, ItemId? AssetId) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>The rule shared by the create body and the link route: the writer reads the asset, and it is an asset.</summary>
public sealed class AssetLinks(IItemRegistry items, IAccessPolicy policy, IActorAccessor actor)
{
    public const string InvalidAssetCode = "subscriptions.invalid_asset";
    public const string AssetHiddenCode = "subscriptions.asset_hidden";

    /// <summary>The reader can read <paramref name="asset"/> (no further check on its kind: it is a stored link).</summary>
    public async Task<bool> CanReadAsync(ItemId asset, CancellationToken ct)
        => actor.Current?.PersonId is { } person && (await policy.CheckAsync(person, asset, AccessKind.Read, ct)).IsSuccess;

    /// <summary>A refusal never tells an unreadable asset from an unknown id or an item that is no asset.</summary>
    public async Task<Failure?> RefusalAsync(ItemId asset, CancellationToken ct)
    {
        var linkable = actor.Current?.PersonId is { } person
                       && (await policy.CheckAsync(person, asset, AccessKind.Read, ct)).IsSuccess
                       && await items.FindAsync(asset, ct) is { Module: "assets", Kind: "asset" };
        return linkable ? null : FieldErrors.At(new Failure(InvalidAssetCode, "This is not an asset you can read.", null), "assetId");
    }
}

[RequestHandler<SetSubscriptionAssetCommand, ItemId>]
public sealed class SetSubscriptionAssetHandler(ISubscriptionStore store, AssetLinks links) : IRequestHandler<SetSubscriptionAssetCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(SetSubscriptionAssetCommand command, CancellationToken ct = default)
    {
        var stored = (await store.AssetsOfAsync([command.ItemId], ct)).TryGetValue(command.ItemId, out var current) ? current : (ItemId?)null;
        // A link to an asset the writer cannot see is neither replaced nor removed: it would be dropped without them knowing it exists.
        if (stored is { } hidden && hidden != command.AssetId && !await links.CanReadAsync(hidden, ct))
            return Result.Failure<ItemId>(FieldErrors.At(
                new Failure(AssetLinks.AssetHiddenCode, "This subscription is linked to an asset you cannot see.", null), "assetId"));
        // Keeping the link it already has is not a new link.
        if (command.AssetId is { } asset && stored != asset && await links.RefusalAsync(asset, ct) is { } refused)
            return Result.Failure<ItemId>(refused);

        var result = await store.SetAssetAsync(command.ItemId, command.AssetId, ct);
        return result.TryGetFailure(out var failure) ? Result.Failure<ItemId>(failure!) : Result.Success(command.ItemId);
    }
}

/// <summary>#648: what an asset's page lists, through the port Assets reads: the reader's visible subscriptions linked to each item.</summary>
public sealed class SubscriptionsOfItems(SubscriptionSet set, ISubscriptionStore store, IHouseholdCalendar calendar, IItemRegistry items, IFxRates fx) : ISubscriptionsOfItems
{
    public async Task<IReadOnlyDictionary<ItemId, IReadOnlyList<LinkedSubscription>>> ForAsync(
        PersonId reader, VisibilityScope scope, IReadOnlyCollection<ItemId> items, CancellationToken ct)
    {
        var linked = await store.SubscriptionsOfAssetsAsync(items, ct);
        if (linked.Count == 0)
            return new Dictionary<ItemId, IReadOnlyList<LinkedSubscription>>();

        var today = await calendar.TodayAsync(ct);
        var wanted = linked.Values.SelectMany(s => s).ToHashSet();
        // Visible ones only, with their timeline: one load whatever the number of items.
        var built = (await set.LoadAsync(reader, scope, today, Horizons.Resolve(today, null), ct, wanted: wanted))
            .ToDictionary(b => b.Input.Subscription.Id);
        return linked.ToDictionary(
            l => l.Key,
            l => (IReadOnlyList<LinkedSubscription>)l.Value.Where(built.ContainsKey).Select(id => Of(built[id])).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
    }

    public async Task<IReadOnlyDictionary<ItemId, IReadOnlyList<ItemSubscriptionCost>>> CostsAsync(
        PersonId reader, VisibilityScope scope, IReadOnlyCollection<ItemId> targets, LocalDate from, LocalDate to, CancellationToken ct)
    {
        var linked = await store.SubscriptionsOfAssetsAsync(targets, ct);
        if (linked.Count == 0)
            return new Dictionary<ItemId, IReadOnlyList<ItemSubscriptionCost>>();

        var today = await calendar.TodayAsync(ct);
        // An archived subscription (a former insurer) still cost what it charged before its archive day.
        var built = (await set.LoadAsync(reader, scope, today, to > today ? to : today, ct, includeArchived: true, wanted: linked.Values.SelectMany(s => s).ToHashSet()))
            .ToDictionary(b => b.Input.Subscription.Id);
        // The charges SubscriptionExpectedCharges expects: a phase's, in the period (a purchase is a one-off, not a running cost).
        var charged = built.Values.ToDictionary(b => b.Input.Subscription.Id, b => b.Timeline.Charges
            .Where(c => c.Origin is ChargeOrigin.FromPhase && c.Date >= from && c.Date <= to).Select(c => c.Price).ToList());
        var access = await items.FindManyAsync(linked.Keys.ToList(), ct);
        var currencies = charged.Values.SelectMany(p => p).Select(p => p.Currency)
            .Concat(access.Values.Select(a => Currency.TryCreate(a.Currency ?? "", out var c) ? (Currency?)c : null).OfType<Currency>()).Distinct().ToList();
        var rates = currencies.Count == 0 ? RateTable.Empty(to, 0) : await fx.LoadAsync(currencies, to, to, ct);

        var result = new Dictionary<ItemId, IReadOnlyList<ItemSubscriptionCost>>();
        foreach (var (target, ids) in linked)
        {
            if (!access.TryGetValue(target, out var item) || !Currency.TryCreate(item.Currency ?? "", out var currency))
                continue;
            result[target] = ids.Where(built.ContainsKey).Select(id => new ItemSubscriptionCost(id, built[id].DisplayName, Total(charged[id], currency)))
                .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        return result;

        Money? Total(IReadOnlyList<Money> prices, Currency currency)
        {
            var sum = 0m;
            foreach (var price in prices)
            {
                if (!rates.TryConvert(price.Amount, price.Currency, currency, to, projected: false, out var converted, out _))
                    return null;
                sum += converted;
            }
            return Money.Create(decimal.Round(sum, Money.MaxDecimals), currency).TryGetValue(out var m) ? m : null;
        }
    }

    private static LinkedSubscription Of(BuiltSubscription b)
    {
        var next = b.Timeline.NextCharge();
        var phase = next is null ? null : ChargeRules.PhaseOf(b.Input, next);
        return new LinkedSubscription(b.Input.Subscription.Id, b.DisplayName, next?.Price, phase?.Every, next?.Date);
    }
}
