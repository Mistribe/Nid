using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
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
    string? Note) : ICommand<ItemId>;

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
public sealed class CreateSubscriptionHandler(IItemRegistry items, ISubscriptionStore store)
    : IRequestHandler<CreateSubscriptionCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(CreateSubscriptionCommand command, CancellationToken ct = default)
    {
        // The Core owns name, owners and visibility, and enforces the ownership invariants.
        var created = await items.CreateAsync(
            new NewItem("subscriptions", "subscription", command.DisplayName, command.Visibility, null, command.Owners), ct);
        if (!created.TryGetValue(out var id))
            return created;

        var stored = await store.CreateAsync(
            id, new NewSubscription(command.ProviderKey, command.AutoRenew, command.Users, command.Note), ct);
        return stored.TryGetFailure(out var failure) ? Result.Failure<ItemId>(failure!) : Result.Success(id);
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
public sealed class AddPurchaseHandler(ISubscriptionStore store) : IRequestHandler<AddPurchaseCommand, PurchaseId>
{
    public async ValueTask<Result<PurchaseId>> HandleAsync(AddPurchaseCommand command, CancellationToken ct = default)
        => await store.AddPurchaseAsync(command.Purchase, ct);
}

[RequestHandler<DeletePurchaseCommand, PurchaseId>]
public sealed class DeletePurchaseHandler(ISubscriptionStore store) : IRequestHandler<DeletePurchaseCommand, PurchaseId>
{
    public async ValueTask<Result<PurchaseId>> HandleAsync(DeletePurchaseCommand command, CancellationToken ct = default)
    {
        var result = await store.DeletePurchaseAsync(command.PurchaseId, ct);
        return result.TryGetFailure(out var failure) ? Result.Failure<PurchaseId>(failure!) : Result.Success(command.PurchaseId);
    }
}
