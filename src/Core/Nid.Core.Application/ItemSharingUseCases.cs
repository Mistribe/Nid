using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application;

/// <summary>
/// #294: an item's sharing as its « Partage » dialog shows it: <c>shared</c> or <c>private</c>, <c>view</c> or <c>edit</c>.
/// <paramref name="CanChange"/>: the actor may change it (an owner, or any adult on an item of dependents only; editing is not owning).
/// <paramref name="IsOwner"/>: the actor is one of its owners, what the detail page's badge says.
/// </summary>
public sealed record ItemSharingView(string Visibility, string ShareLevel, bool CanChange, bool IsOwner)
{
    public static ItemSharingView Of(Visibility visibility, ShareLevel level, IReadOnlyList<Owner> owners, PersonId actor, IItemRules rules)
        => new(visibility == Nid.Core.Contracts.Visibility.Private ? "private" : "shared",
            level == Nid.Core.Contracts.ShareLevel.Edit && visibility == Nid.Core.Contracts.Visibility.Shared ? "edit" : "view",
            rules.CanCreate(actor, owners), owners.Any(o => o.PersonId == actor));
}

public sealed record GetItemSharingQuery(ItemId ItemId) : IQuery<ItemSharingView>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

/// <summary>#294: only whoever owns the item changes its level (<c>authorization.md</c> §5), not an adult who may only edit it.</summary>
public sealed record SetItemSharingCommand(ItemId ItemId, ShareLevel ShareLevel) : ICommand<ItemSharingView>, IItemScoped
{
    public AccessKind Access => AccessKind.Own;
}

[RequestHandler<GetItemSharingQuery, ItemSharingView>]
public sealed class GetItemSharingHandler(IItemRegistry items, IItemRules rules, IActorAccessor actor)
    : IRequestHandler<GetItemSharingQuery, ItemSharingView>
{
    public async ValueTask<Result<ItemSharingView>> HandleAsync(GetItemSharingQuery query, CancellationToken ct = default)
        => await items.FindAsync(query.ItemId, ct) is { } item
            ? Result.Success(ItemSharingView.Of(item.Visibility, item.ShareLevel, item.Owners, actor.Current!.PersonId!.Value, rules))
            : Result.Failure<ItemSharingView>(new NotFoundFailure("item", query.ItemId.ToString()));
}

[RequestHandler<SetItemSharingCommand, ItemSharingView>]
public sealed class SetItemSharingHandler(IItemRegistry items, IItemRules rules, IActorAccessor actor) : IRequestHandler<SetItemSharingCommand, ItemSharingView>
{
    public async ValueTask<Result<ItemSharingView>> HandleAsync(SetItemSharingCommand command, CancellationToken ct = default)
    {
        if (await items.FindAsync(command.ItemId, ct) is not { } item)
            return Result.Failure<ItemSharingView>(new NotFoundFailure("item", command.ItemId.ToString()));

        var set = await items.SetShareLevelAsync(command.ItemId, command.ShareLevel, ct);
        return set.TryGetFailure(out var failure)
            ? Result.Failure<ItemSharingView>(failure!)
            : Result.Success(ItemSharingView.Of(item.Visibility, command.ShareLevel, item.Owners, actor.Current!.PersonId!.Value, rules));
    }
}
