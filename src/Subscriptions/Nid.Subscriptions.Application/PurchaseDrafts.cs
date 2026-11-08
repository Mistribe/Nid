using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Subscriptions.Application;

/// <summary>A grant as a request carries it: without a subscription, it covers the one the request is about (1E2b1 design §2.4).</summary>
public sealed record GrantDraft(ItemId? SubscriptionId, Duration Duration, CoverageSource Source, LocalDate? StartsOn);

/// <summary>A purchase before it knows the subscription it belongs to: the creation learns it only once the item exists.</summary>
public sealed record PurchaseDraft(
    LocalDate Date, string Label, Money Price, Payer Payer, PurchaseKind Kind,
    IReadOnlyList<GrantDraft> Grants, decimal? AllocatedAmount, string? Note)
{
    public Purchase ToPurchase(ItemId self) => new(
        PurchaseId.New(), Date, Label, Price, Payer, Kind,
        Grants.Select(g => new Grant(g.SubscriptionId ?? self, g.Duration, g.Source, g.StartsOn)).ToList(),
        AllocatedAmount);
}

/// <summary>Where an engine refusal of a purchase points in a request (1E2b1 design §3.1 step 3).</summary>
public static class PurchasePaths
{
    public static string? Field(string code) => code switch
    {
        "subscriptions.price_negative" => "price.amount",
        "subscriptions.allocated_not_bundle" or "subscriptions.allocated_range" or "subscriptions.allocated_scale" => "allocatedAmount",
        "subscriptions.duration_count" => "grants",
        _ => null,
    };

    /// <summary>The refusal at its path under <paramref name="prefix"/> ("" on the purchase route, "purchases[i]" on the creation).</summary>
    public static Failure Locate(Failure failure, string prefix)
    {
        var field = Field(failure.Code);
        var path = (prefix, field) switch
        {
            ("", null) => null,
            ("", { } f) => f,
            ({ } p, null) => p,
            ({ } p, { } f) => $"{p}.{f}",
        };
        return path is null ? failure : FieldErrors.At(failure, path);
    }
}

/// <summary>
/// Every subscription a draft's grants name, other than the one the request is about, must be writable by the actor,
/// checked before anything is written. A refusal reads as a missing subscription (authorization.md §6), at its grant.
/// </summary>
public static class GrantTargets
{
    public const string Code = "subscriptions.not_found";
    private const string Message = "No subscription with this id can be written.";

    public static async Task<Failure?> RefusedAsync(
        IAccessPolicy policy, Actor actor, PurchaseDraft draft, ItemId? self, string prefix, CancellationToken ct)
    {
        // A system actor is not bound to a person and has no rights to check (ItemWriteGuard does the same).
        if (actor.PersonId is not { } person)
            return null;
        for (var j = 0; j < draft.Grants.Count; j++)
        {
            if (draft.Grants[j].SubscriptionId is not { } target || target == self)
                continue;
            if ((await policy.CheckAsync(person, target, AccessKind.Write, ct)).TryGetFailure(out _))
            {
                var path = prefix == "" ? $"grants[{j}].subscriptionId" : $"{prefix}.grants[{j}].subscriptionId";
                return FieldErrors.At(new Failure(Code, Message), path);
            }
        }
        return null;
    }
}

/// <summary>The engine's rules on one purchase alone, located (1E2b1 design §3.2).</summary>
public static class PurchaseRules
{
    public static Failure? Check(ItemId self, Purchase purchase, string prefix)
    {
        var input = new SubscriptionInput(new SubscriptionHeader(self, true, null), [], [purchase]);
        return SubscriptionTimeline.Build(input, purchase.Date, purchase.Date).TryGetFailure(out var refused)
            ? PurchasePaths.Locate(refused!, prefix)
            : null;
    }
}
