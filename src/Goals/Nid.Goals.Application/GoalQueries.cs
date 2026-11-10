using Nid.Core.Contracts;
using Nid.Goals.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Goals.Application;

/// <summary>Spec §5: the goals visible in the scope, open ones only unless <paramref name="Closed"/> (then closed ones too).</summary>
public sealed record ListGoalsQuery(VisibilityScope Scope, bool Closed) : IQuery<GoalList>;

/// <summary>Spec §5: one goal; its figures count the envelopes the reader sees in <paramref name="Scope"/> (D1).</summary>
public sealed record GetGoalQuery(ItemId ItemId, VisibilityScope Scope) : IQuery<GoalDetail>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

/// <summary>#23: what deleting a goal removes, read before the delete.</summary>
public sealed record GoalDeletionQuery(ItemId ItemId) : IQuery<ItemDeletionPreview>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

[RequestHandler<GoalDeletionQuery, ItemDeletionPreview>]
public sealed class GoalDeletionHandler(IItemRegistry items, IItemDeletions deletions) : IRequestHandler<GoalDeletionQuery, ItemDeletionPreview>
{
    public async ValueTask<Result<ItemDeletionPreview>> HandleAsync(GoalDeletionQuery query, CancellationToken ct = default)
    {
        var item = await items.FindAsync(query.ItemId, ct);
        var preview = item is { Module: "goals" } ? await deletions.PreviewAsync(query.ItemId, ct) : null;
        return preview is null
            ? Result.Failure<ItemDeletionPreview>(new NotFoundFailure("goal", query.ItemId.ToString()))
            : Result.Success(preview);
    }
}

/// <summary>Issue #412: the target an emergency fund of <paramref name="Months"/> would have, before it is created.</summary>
public sealed record EmergencyTargetQuery(int Months, VisibilityScope Scope) : IQuery<EmergencyTargetView>;

[RequestHandler<EmergencyTargetQuery, EmergencyTargetView>]
public sealed class EmergencyTargetHandler(GoalReader reader, IActorAccessor actor) : IRequestHandler<EmergencyTargetQuery, EmergencyTargetView>
{
    public async ValueTask<Result<EmergencyTargetView>> HandleAsync(EmergencyTargetQuery query, CancellationToken ct = default)
        => query.Months is < 1 or > 24
            ? Result.Failure<EmergencyTargetView>(new Failure(GoalsCodes.InvalidMonths, "An emergency fund covers 1 to 24 months.",
                new Dictionary<string, object?> { ["field"] = "months" }))
            : await reader.EmergencyTargetAsync(actor.Current!.PersonId!.Value, query.Scope, query.Months, ct);
}

[RequestHandler<ListGoalsQuery, GoalList>]
public sealed class ListGoalsHandler(IVisibleItems visible, IItemRegistry items, IGoalStore goals, GoalReader reader, IActorAccessor actor)
    : IRequestHandler<ListGoalsQuery, GoalList>
{
    public async ValueTask<Result<GoalList>> HandleAsync(ListGoalsQuery query, CancellationToken ct = default)
    {
        var person = actor.Current!.PersonId!.Value;
        // The 3-argument ForAsync returns active items only: closed goals are asked for explicitly (D12).
        var ids = await visible.ForAsync(person, query.Scope, includeArchived: query.Closed, ct);
        var found = await goals.FindManyAsync(ids.ToList(), ct);
        var registered = await items.FindManyAsync(found.Select(g => g.ItemId).ToList(), ct);
        var read = await reader.ReadAsync(person, query.Scope,
            found.Where(g => registered.ContainsKey(g.ItemId)).Select(g => (g, registered[g.ItemId])).ToList(), ct);
        if (!read.TryGetValue(out var details))
            return Result.Failure<GoalList>(read.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
        return Result.Success(new GoalList(GoalReader.Ordered(details!.Select(d => d.Goal))));
    }
}

[RequestHandler<GetGoalQuery, GoalDetail>]
public sealed class GetGoalHandler(IItemRegistry items, IGoalStore goals, GoalReader reader, IActorAccessor actor)
    : IRequestHandler<GetGoalQuery, GoalDetail>
{
    public async ValueTask<Result<GoalDetail>> HandleAsync(GetGoalQuery query, CancellationToken ct = default)
    {
        var loaded = await GoalCheck.LoadAsync(goals, items, query.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<GoalDetail>(failure!);
        loaded.TryGetValue(out var found);
        var read = await reader.ReadAsync(actor.Current!.PersonId!.Value, query.Scope, [found], ct);
        if (!read.TryGetValue(out var details))
            return Result.Failure<GoalDetail>(read.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());
        return Result.Success(details![0]);
    }
}
