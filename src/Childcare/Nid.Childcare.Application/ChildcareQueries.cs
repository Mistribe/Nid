using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Childcare.Application;

/// <summary>Spec §5: the arrangements visible in the scope, open ones only unless <paramref name="Closed"/> (then closed ones too).</summary>
public sealed record ListChildcareQuery(VisibilityScope Scope, bool Closed) : IQuery<ChildcareList>;

public sealed record GetChildcareQuery(ItemId ItemId) : IQuery<ChildcareDetail>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

[RequestHandler<ListChildcareQuery, ChildcareList>]
public sealed class ListChildcareHandler(IVisibleItems visible, IItemRegistry items, IChildcareStore store, ChildcareReader reader, IActorAccessor actor)
    : IRequestHandler<ListChildcareQuery, ChildcareList>
{
    public async ValueTask<Result<ChildcareList>> HandleAsync(ListChildcareQuery query, CancellationToken ct = default)
    {
        var person = actor.Current!.PersonId!.Value;
        var ids = await visible.ForAsync(person, query.Scope, includeArchived: query.Closed, ct);
        var found = await store.FindManyAsync(ids.ToList(), ct);
        var registered = await items.FindManyAsync(found.Select(a => a.ItemId).ToList(), ct);
        var details = await reader.ReadAsync(person, found.Where(a => registered.ContainsKey(a.ItemId)).Select(a => (a, registered[a.ItemId])).ToList(), ct);
        return Result.Success(new ChildcareList(ChildcareReader.Ordered(details.Select(d => d.Summary))));
    }
}

[RequestHandler<GetChildcareQuery, ChildcareDetail>]
public sealed class GetChildcareHandler(IItemRegistry items, IChildcareStore store, ChildcareReader reader, IActorAccessor actor)
    : IRequestHandler<GetChildcareQuery, ChildcareDetail>
{
    public async ValueTask<Result<ChildcareDetail>> HandleAsync(GetChildcareQuery query, CancellationToken ct = default)
    {
        var loaded = await ChildcareCheck.LoadAsync(store, items, query.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ChildcareDetail>(failure!);
        loaded.TryGetValue(out var found);
        return Result.Success((await reader.ReadAsync(actor.Current!.PersonId!.Value, [(found.Arrangement, found.Item)], ct))[0]);
    }
}
