using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
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
    string? Note,
    BillingPhase? Phase = null) : ICommand<ItemId>, ICreatesItem;

public sealed record EditSubscriptionCommand(
    ItemId ItemId, bool? AutoRenew, LocalDate? CancelledAt, bool ClearCancelledAt, string? Note,
    string? ProviderKey = null, bool ClearProviderKey = false)
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

public sealed record AddPurchaseCommand(ItemId ItemId, PurchaseDraft Purchase) : ICommand<PurchaseId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record DeletePurchaseCommand(ItemId ItemId, PurchaseId PurchaseId) : ICommand<PurchaseId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

[RequestHandler<CreateSubscriptionCommand, ItemId>]
public sealed class CreateSubscriptionHandler(
    IItemRegistry items, ISubscriptionStore store, IUnitOfWork unitOfWork, ProviderAccess providers, IActorAccessor actor)
    : IRequestHandler<CreateSubscriptionCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(CreateSubscriptionCommand command, CancellationToken ct = default)
    {
        // Design §5: the key names a provider the actor can see and that is not archived. Checked before anything is
        // written, so a refused key leaves no Core item behind.
        if (command.ProviderKey is { } key
            && (await providers.EnsureLinkableAsync(actor.Current?.PersonId, key, ct)).TryGetFailure(out var unknown))
            return Result.Failure<ItemId>(FieldErrors.At(unknown!, "providerKey"));

        // The Core item and the subscription row are two contexts: one transaction, or an orphan item when the
        // second write fails.
        await using var transaction = await unitOfWork.BeginAsync(ct);

        // The Core owns name, owners and visibility, and enforces the ownership invariants.
        var created = await items.CreateAsync(
            new NewItem("subscriptions", "subscription", command.DisplayName, command.Visibility, null, command.Owners), ct);
        if (!created.TryGetValue(out var id))
            return created.TryGetFailure(out var refusedOwners) ? Result.Failure<ItemId>(FieldErrors.At(refusedOwners!, "owners")) : created;

        var stored = await store.CreateAsync(
            id, new NewSubscription(command.ProviderKey, command.AutoRenew, command.Users, command.Note), ct);
        if (stored.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);

        if (command.Phase is { } phase)
        {
            // The engine's rules on the phase alone, before it is written: a refusal rolls the item back with it.
            var input = new SubscriptionInput(new SubscriptionHeader(id, command.AutoRenew, null), [phase], []);
            if (SubscriptionTimeline.Build(input, phase.StartsOn, phase.StartsOn).TryGetFailure(out var refused))
                return Result.Failure<ItemId>(FieldErrors.At(refused!, PhasePaths.Of(refused!.Code, "phase")));
            var added = await store.AddPhaseAsync(id, phase, RowOrigin.None, ct);
            if (added.TryGetFailure(out var notAdded))
                return Result.Failure<ItemId>(notAdded!);
        }

        await transaction.CommitAsync(ct);
        return Result.Success(id);
    }
}

[RequestHandler<EditSubscriptionCommand, ItemId>]
public sealed class EditSubscriptionHandler(ISubscriptionStore store, ProviderAccess providers, IActorAccessor actor)
    : IRequestHandler<EditSubscriptionCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(EditSubscriptionCommand command, CancellationToken ct = default)
    {
        if (command.ProviderKey is { } key && !command.ClearProviderKey)
        {
            // Keeping the provider it already has is not a new link, even an archived one (design §5).
            var current = (await store.ProviderKeysAsync([command.ItemId], ct)).GetValueOrDefault(command.ItemId);
            if (current != key && (await providers.EnsureLinkableAsync(actor.Current?.PersonId, key, ct)).TryGetFailure(out var unknown))
                return Result.Failure<ItemId>(unknown!);
        }

        var result = await store.EditAsync(command.ItemId, new SubscriptionEdit(
            command.AutoRenew, command.CancelledAt, command.ClearCancelledAt, command.Note, command.ProviderKey, command.ClearProviderKey), ct);
        return result.TryGetFailure(out var failure) ? Result.Failure<ItemId>(failure!) : Result.Success(command.ItemId);
    }
}

[RequestHandler<OpenPhaseCommand, BillingPhaseId>]
public sealed class OpenPhaseHandler(ISubscriptionStore store) : IRequestHandler<OpenPhaseCommand, BillingPhaseId>
{
    public async ValueTask<Result<BillingPhaseId>> HandleAsync(OpenPhaseCommand command, CancellationToken ct = default)
        => await store.AddPhaseAsync(command.ItemId, command.Phase, RowOrigin.None, ct);
}

[RequestHandler<ClosePhaseCommand, BillingPhaseId>]
public sealed class ClosePhaseHandler(ISubscriptionStore store) : IRequestHandler<ClosePhaseCommand, BillingPhaseId>
{
    public async ValueTask<Result<BillingPhaseId>> HandleAsync(ClosePhaseCommand command, CancellationToken ct = default)
    {
        var result = await store.ClosePhaseAsync(command.ItemId, command.PhaseId, command.EndsOn, null, ct);
        return result.TryGetFailure(out var failure) ? Result.Failure<BillingPhaseId>(failure!) : Result.Success(command.PhaseId);
    }
}

[RequestHandler<AddPurchaseCommand, PurchaseId>]
public sealed class AddPurchaseHandler(ISubscriptionStore store, IAccessPolicy policy, IActorAccessor actor)
    : IRequestHandler<AddPurchaseCommand, PurchaseId>
{
    public async ValueTask<Result<PurchaseId>> HandleAsync(AddPurchaseCommand command, CancellationToken ct = default)
    {
        // The route's item is the only one ItemAccessBehavior saw; the grants may name any other (1E2b1 design §3.2).
        if (await GrantTargets.RefusedAsync(policy, actor.Current!, command.Purchase, command.ItemId, "", ct) is { } refused)
            return Result.Failure<PurchaseId>(refused);

        var purchase = command.Purchase.ToPurchase(command.ItemId);
        if (PurchaseRules.Check(command.ItemId, purchase, "") is { } broken)
            return Result.Failure<PurchaseId>(broken);

        return await store.AddPurchaseAsync(purchase, new RowOrigin(Note: command.Purchase.Note), ct);
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

public sealed record ChangePriceCommand(
    ItemId ItemId, LocalDate StartsOn, Money Price, Core.Contracts.Duration? Every, Core.Contracts.Duration? Covers, int? MaxCycles, Payer? Payer)
    : ICommand<BillingPhaseId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>
/// 1E2a design §2.3: the phase open on <see cref="ChangePriceCommand.StartsOn"/> ends there and the next one starts there,
/// in one transaction; what the change leaves out is taken from the phase it replaces.
/// </summary>
[RequestHandler<ChangePriceCommand, BillingPhaseId>]
public sealed class ChangePriceHandler(ISubscriptionStore store, IUnitOfWork unitOfWork) : IRequestHandler<ChangePriceCommand, BillingPhaseId>
{
    public async ValueTask<Result<BillingPhaseId>> HandleAsync(ChangePriceCommand command, CancellationToken ct = default)
    {
        var input = await store.LoadAsync(command.ItemId, ct);
        if (input is null)
            return Result.Failure<BillingPhaseId>(new NotFoundFailure("subscription", command.ItemId.ToString()));

        var open = input.Phases.FirstOrDefault(p => p.StartsOn <= command.StartsOn && (p.EndsOn is null || p.EndsOn > command.StartsOn));
        if (open is null)
            return Refuse("subscriptions.no_open_phase", "No phase is open on this date: add a phase instead.");
        if (command.StartsOn <= open.StartsOn)
            return Refuse("subscriptions.phase_range", "A price change starts after the phase it replaces.");

        // A cycle limit is shared out, never dropped (final review): the closed phase keeps the cycles it charged before
        // the change, the new one the rest; a phase that charged them all is over, whatever its dates say.
        int? spent = null;
        if (open.MaxCycles is { } limit)
        {
            var before = SubscriptionTimeline.Build(input, command.StartsOn, command.StartsOn);
            spent = before.TryGetValue(out var walked)
                ? walked!.Charges.Count(c => c.Origin is ChargeOrigin.FromPhase f && f.PhaseId == open.Id && c.Date < command.StartsOn)
                : 0;
            if (spent >= limit)
                return Refuse("subscriptions.no_open_phase", "No phase is open on this date: add a phase instead.");
        }

        var every = command.Every ?? open.Every;
        var next = new BillingPhase(
            BillingPhaseId.New(), command.StartsOn, open.EndsOn, command.Price, every,
            command.Covers ?? (command.Every is null ? open.Covers : null),
            command.MaxCycles ?? (open.MaxCycles - spent), command.Payer ?? open.Payer);

        var after = input with { Phases = input.Phases.Select(p => p.Id == open.Id ? p with { EndsOn = command.StartsOn, MaxCycles = spent ?? p.MaxCycles } : p).Append(next).ToList() };
        if (SubscriptionTimeline.Build(after, command.StartsOn, command.StartsOn).TryGetFailure(out var refused))
            // The change's own fields: an overlap or anything about the phase as a whole is its start date.
            return Result.Failure<BillingPhaseId>(FieldErrors.At(refused!, PhasePaths.Field(refused!.Code) ?? "startsOn"));

        await using var transaction = await unitOfWork.BeginAsync(ct);
        var closed = await store.ClosePhaseAsync(command.ItemId, open.Id, command.StartsOn, spent, ct);
        if (closed.TryGetFailure(out var notClosed))
            return Result.Failure<BillingPhaseId>(notClosed!);
        var added = await store.AddPhaseAsync(command.ItemId, next, RowOrigin.None, ct);
        if (added.TryGetFailure(out var notAdded))
            return Result.Failure<BillingPhaseId>(notAdded!);
        await transaction.CommitAsync(ct);
        return Result.Success(next.Id);

        static Result<BillingPhaseId> Refuse(string code, string message)
            => Result.Failure<BillingPhaseId>(FieldErrors.At(new Failure(code, message, null), "startsOn"));
    }
}
