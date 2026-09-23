using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Subscriptions.Application;

// Create / OpenPhase / AddPurchase were started early (Task 4): its own acceptance test reaches through the
// real HTTP API to create subscriptions, open phases and record purchases (see NidApp.NewSubscriptionAsync /
// NewMonthlyAsync / NewSpotifyGoldenCaseAsync / NewSharedBundleAsync in
// tests/Nid.Subscriptions.IntegrationTests/NidApp.cs), so that slice of writing could not be deferred.
// EditSubscriptionCommand, ClosePhaseCommand and DeletePurchaseCommand complete the write side (Task 5).

public sealed record CreateSubscriptionCommand(
    string DisplayName,
    Visibility Visibility,
    IReadOnlyList<OwnerShare> Owners,
    string? ProviderKey,
    bool AutoRenew,
    IReadOnlyList<PersonId> Users,
    string? Note) : ICommand<ItemId>, ICreatesItem;

public sealed record EditSubscriptionCommand(
    ItemId ItemId, bool? AutoRenew, LocalDate? CancelledAt, bool ClearCancelledAt, string? Note)
    : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record OpenPhaseCommand(ItemId ItemId, BillingPhase Phase) : ICommand<BillingPhaseId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record ClosePhaseCommand(ItemId ItemId, BillingPhaseId PhaseId, LocalDate EndsOn)
    : ICommand<BillingPhaseId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record AddPurchaseCommand(ItemId ItemId, Purchase Purchase) : ICommand<PurchaseId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record DeletePurchaseCommand(ItemId ItemId, PurchaseId PurchaseId) : ICommand<PurchaseId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

[RequestHandler<CreateSubscriptionCommand, ItemId>]
public sealed class CreateSubscriptionHandler(IItemRegistry items, ISubscriptionStore store, IUnitOfWork unitOfWork)
    : IRequestHandler<CreateSubscriptionCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(CreateSubscriptionCommand command, CancellationToken ct = default)
    {
        // The Core item and the subscription row are two contexts: one transaction, or an orphan item when the
        // second write fails.
        await using var transaction = await unitOfWork.BeginAsync(ct);

        // The Core owns name, owners and visibility, and enforces the ownership invariants.
        var created = await items.CreateAsync(
            new NewItem("subscriptions", "subscription", command.DisplayName, command.Visibility, null, command.Owners), ct);
        if (!created.TryGetValue(out var id))
            return created;

        var stored = await store.CreateAsync(
            id, new NewSubscription(command.ProviderKey, command.AutoRenew, command.Users, command.Note), ct);
        if (stored.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);

        await transaction.CommitAsync(ct);
        return Result.Success(id);
    }
}

[RequestHandler<EditSubscriptionCommand, ItemId>]
public sealed class EditSubscriptionHandler(ISubscriptionStore store) : IRequestHandler<EditSubscriptionCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(EditSubscriptionCommand command, CancellationToken ct = default)
    {
        var result = await store.EditAsync(
            command.ItemId, new SubscriptionEdit(command.AutoRenew, command.CancelledAt, command.ClearCancelledAt, command.Note), ct);
        return result.TryGetFailure(out var failure) ? Result.Failure<ItemId>(failure!) : Result.Success(command.ItemId);
    }
}

[RequestHandler<OpenPhaseCommand, BillingPhaseId>]
public sealed class OpenPhaseHandler(ISubscriptionStore store) : IRequestHandler<OpenPhaseCommand, BillingPhaseId>
{
    public async ValueTask<Result<BillingPhaseId>> HandleAsync(OpenPhaseCommand command, CancellationToken ct = default)
        => await store.AddPhaseAsync(command.ItemId, command.Phase, ct);
}

[RequestHandler<ClosePhaseCommand, BillingPhaseId>]
public sealed class ClosePhaseHandler(ISubscriptionStore store) : IRequestHandler<ClosePhaseCommand, BillingPhaseId>
{
    public async ValueTask<Result<BillingPhaseId>> HandleAsync(ClosePhaseCommand command, CancellationToken ct = default)
    {
        var result = await store.ClosePhaseAsync(command.ItemId, command.PhaseId, command.EndsOn, ct);
        return result.TryGetFailure(out var failure) ? Result.Failure<BillingPhaseId>(failure!) : Result.Success(command.PhaseId);
    }
}

[RequestHandler<AddPurchaseCommand, PurchaseId>]
public sealed class AddPurchaseHandler(ISubscriptionStore store, IAccessPolicy policy, IActorAccessor actor)
    : IRequestHandler<AddPurchaseCommand, PurchaseId>
{
    public async ValueTask<Result<PurchaseId>> HandleAsync(AddPurchaseCommand command, CancellationToken ct = default)
    {
        // The route's item is the only one ItemAccessBehavior saw; the body may name any other.
        var guard = await ItemWriteGuard.EnsureWritableAsync(
            policy, actor.Current!, command.Purchase.Grants.Select(g => g.SubscriptionId),
            item => new NotFoundFailure("item", item.ToString()), ct);
        if (guard.TryGetFailure(out var refused))
            return Result.Failure<PurchaseId>(refused!);

        return await store.AddPurchaseAsync(command.Purchase, ct);
    }
}

[RequestHandler<DeletePurchaseCommand, PurchaseId>]
public sealed class DeletePurchaseHandler(ISubscriptionStore store, IAccessPolicy policy, IActorAccessor actor)
    : IRequestHandler<DeletePurchaseCommand, PurchaseId>
{
    public async ValueTask<Result<PurchaseId>> HandleAsync(DeletePurchaseCommand command, CancellationToken ct = default)
    {
        // Removing the purchase cascades into every grant it carries, including grants to items the actor
        // cannot even read: the whole cascade is checked before any of it happens.
        var guard = await ItemWriteGuard.EnsureWritableAsync(
            policy, actor.Current!, await store.GrantedItemsAsync(command.PurchaseId, ct),
            _ => new NotFoundFailure("purchase", command.PurchaseId.ToString()), ct);
        if (guard.TryGetFailure(out var refused))
            return Result.Failure<PurchaseId>(refused!);

        var result = await store.DeletePurchaseAsync(command.ItemId, command.PurchaseId, ct);
        return result.TryGetFailure(out var failure) ? Result.Failure<PurchaseId>(failure!) : Result.Success(command.PurchaseId);
    }
}

/// <summary>
/// A purchase's grants are the only place in this module where one write touches a second item: the route
/// names one subscription, the grants may name any other. <c>ItemAccessBehavior</c> checks the route and
/// nothing else, so the items the payload names — and the ones a delete would cascade into — are checked
/// here, at the handler boundary, before the store is asked to write anything. The check is the same
/// <see cref="IAccessPolicy"/> the pipeline uses, so <c>authorization.md</c> §5's <c>Writers</c> stays the
/// single source of truth. Its refusal is always reported as <em>not found</em>, never as forbidden: §6 does
/// not disclose the existence of an item, and a 403 on a second item the actor never named would.
/// </summary>
internal static class ItemWriteGuard
{
    public static async Task<Result> EnsureWritableAsync(
        IAccessPolicy policy,
        Actor actor,
        IEnumerable<ItemId> items,
        Func<ItemId, Failure> refusal,
        CancellationToken ct)
    {
        // A system actor is not bound to a person and so has no rights to check, exactly as
        // ItemAccessBehavior treats it; it never reaches here from an HTTP request.
        if (actor.PersonId is not { } person)
            return Result.Success();

        foreach (var item in items.Distinct())
        {
            var check = await policy.CheckAsync(person, item, AccessKind.Write, ct);
            if (check.TryGetFailure(out _))
                return Result.Failure(refusal(item));
        }

        return Result.Success();
    }
}
