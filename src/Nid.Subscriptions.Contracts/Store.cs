using NodaTime;
using Nid.Core.Contracts;
using UnambitiousFx.Functional;

namespace Nid.Subscriptions.Contracts;

/// <summary>What a subscription needs beyond its Core item, for creation.</summary>
public sealed record NewSubscription(string? ProviderKey, bool AutoRenew, IReadOnlyList<PersonId> Users, string? Note);

/// <summary>Fields of a subscription that can change without touching the Core item.</summary>
public sealed record SubscriptionEdit(bool? AutoRenew, LocalDate? CancelledAt, bool ClearCancelledAt, string? Note);

/// <summary>
/// Rows in, engine input out. The store never computes: coverage and charges are the engine's job, and
/// nothing derived is persisted.
/// </summary>
public interface ISubscriptionStore
{
    Task<SubscriptionInput?> LoadAsync(ItemId id, CancellationToken ct);

    /// <summary>Loads several at once; ids the household does not hold are skipped, not faulted.</summary>
    Task<IReadOnlyList<SubscriptionInput>> LoadManyAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct);

    Task<Result> CreateAsync(ItemId id, NewSubscription subscription, CancellationToken ct);

    Task<Result> EditAsync(ItemId id, SubscriptionEdit edit, CancellationToken ct);

    Task<Result<BillingPhaseId>> AddPhaseAsync(ItemId id, BillingPhase phase, CancellationToken ct);

    Task<Result> ClosePhaseAsync(ItemId id, BillingPhaseId phase, LocalDate endsOn, CancellationToken ct);

    Task<Result<PurchaseId>> AddPurchaseAsync(Purchase purchase, CancellationToken ct);

    Task<Result> DeletePurchaseAsync(PurchaseId purchase, CancellationToken ct);
}
