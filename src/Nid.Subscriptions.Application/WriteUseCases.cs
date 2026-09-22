using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Subscriptions.Application;

// This file is Task 5's ("Writing — commands and the POST/PATCH/DELETE endpoints"), started early: Task 4's
// own acceptance test reaches through the real HTTP API to create subscriptions, open phases and record
// purchases (see NidApp.NewSubscriptionAsync / NewMonthlyAsync / NewSpotifyGoldenCaseAsync / NewSharedBundleAsync
// in tests/Nid.Subscriptions.IntegrationTests/NidApp.cs), so the read-only slice of writing cannot be deferred.
// Only Create / OpenPhase / AddPurchase are implemented here — the read side's own tests need no more.
// EditSubscriptionCommand, ClosePhaseCommand, DeletePurchaseCommand and their PATCH/DELETE routes are still
// Task 5's to add.

public sealed record CreateSubscriptionCommand(
    string DisplayName,
    Visibility Visibility,
    IReadOnlyList<OwnerShare> Owners,
    string? ProviderKey,
    bool AutoRenew,
    IReadOnlyList<PersonId> Users,
    string? Note) : ICommand<ItemId>;

public sealed record OpenPhaseCommand(ItemId ItemId, BillingPhase Phase) : ICommand<BillingPhaseId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record AddPurchaseCommand(ItemId ItemId, Purchase Purchase) : ICommand<PurchaseId>, IItemScoped
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

[RequestHandler<OpenPhaseCommand, BillingPhaseId>]
public sealed class OpenPhaseHandler(ISubscriptionStore store) : IRequestHandler<OpenPhaseCommand, BillingPhaseId>
{
    public async ValueTask<Result<BillingPhaseId>> HandleAsync(OpenPhaseCommand command, CancellationToken ct = default)
        => await store.AddPhaseAsync(command.ItemId, command.Phase, ct);
}

[RequestHandler<AddPurchaseCommand, PurchaseId>]
public sealed class AddPurchaseHandler(ISubscriptionStore store) : IRequestHandler<AddPurchaseCommand, PurchaseId>
{
    public async ValueTask<Result<PurchaseId>> HandleAsync(AddPurchaseCommand command, CancellationToken ct = default)
        => await store.AddPurchaseAsync(command.Purchase, ct);
}
