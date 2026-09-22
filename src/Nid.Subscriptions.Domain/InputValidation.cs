using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Subscriptions.Domain;

/// <summary>
/// What must hold before a timeline can be walked (<c>subscription-data-model.md</c> §"Validation rules").
/// Hard errors fail; softer findings are returned as warnings for the caller to report.
/// </summary>
internal static class InputValidation
{
    public static Result<IReadOnlyList<Finding>> Validate(SubscriptionInput input)
    {
        var phaseIds = new HashSet<BillingPhaseId>();
        foreach (var phase in input.Phases)
            if (!phaseIds.Add(phase.Id))
                return Fail("subscriptions.duplicate_id", "Ids are unique per entity.", phase.Id.ToString());

        var purchaseIds = new HashSet<PurchaseId>();
        foreach (var purchase in input.Purchases)
            if (!purchaseIds.Add(purchase.Id))
                return Fail("subscriptions.duplicate_id", "Ids are unique per entity.", purchase.Id.ToString());

        foreach (var phase in input.Phases)
        {
            if (phase.EndsOn is { } endsOn && endsOn <= phase.StartsOn)
                return Fail("subscriptions.phase_range", "A phase ends after it starts.", phase.Id.ToString());

            if (phase.Every.Count <= 0 || phase.CoverageEvery.Count <= 0)
                return Fail("subscriptions.duration_count", "A duration has a positive count.", phase.Id.ToString());

            if (phase.MaxCycles is <= 0)
                return Fail("subscriptions.max_cycles", "A cycle limit is positive.", phase.Id.ToString());
        }

        for (var i = 0; i < input.Phases.Count; i++)
        for (var j = i + 1; j < input.Phases.Count; j++)
            if (PhasesOverlap(input.Phases[i], input.Phases[j]))
                return Fail("subscriptions.phase_overlap", "Phases of one subscription do not overlap.",
                            input.Phases[i].Id.ToString());

        foreach (var purchase in input.Purchases)
        {
            var signed = purchase.Kind is PurchaseKind.Refund or PurchaseKind.Adjustment;
            if (purchase.Price.Amount < 0 && !signed)
                return Fail("subscriptions.price_negative",
                            "Only a refund or an adjustment carries a negative price.", purchase.Id.ToString());

            if (purchase.AllocatedAmount is { } allocated)
            {
                if (purchase.Kind != PurchaseKind.Bundle)
                    return Fail("subscriptions.allocated_not_bundle",
                                "Only a bundle carries an allocated amount.", purchase.Id.ToString());

                if (allocated < 0 || allocated > purchase.Price.Amount)
                    return Fail("subscriptions.allocated_range",
                                "An allocated amount lies between zero and the price.", purchase.Id.ToString());

                // An allocated amount out of Money's scale would be refused by Money.Create later, where the
                // only recourse left is to fall back to the whole bundle price. Refuse it here instead.
                if (decimal.Round(allocated, Money.MaxDecimals) != allocated)
                    return Fail("subscriptions.allocated_scale",
                                $"An allocated amount has at most {Money.MaxDecimals} decimals.",
                                purchase.Id.ToString());
            }

            foreach (var grant in purchase.Grants)
            {
                // A grant for another subscription is legitimate: the core principle of the data model is
                // that one payment may grant coverage to several services. The timeline walk keeps only the
                // grants of the subscription it is building.
                if (grant.Duration.Count <= 0)
                    return Fail("subscriptions.duration_count", "A duration has a positive count.",
                                purchase.Id.ToString());
            }
        }

        var warnings = new List<Finding>();
        if (input.Phases.Count == 0 && input.Purchases.Count == 0)
            warnings.Add(new Finding("subscriptions.incomplete",
                                     "This subscription has no phase and no purchase.",
                                     input.Subscription.Id.ToString()));

        return Result.Success<IReadOnlyList<Finding>>(warnings);
    }

    private static bool PhasesOverlap(BillingPhase a, BillingPhase b)
        => (a.EndsOn is not { } aEnd || b.StartsOn < aEnd)
           && (b.EndsOn is not { } bEnd || a.StartsOn < bEnd);

    private static Result<IReadOnlyList<Finding>> Fail(string code, string message, string? reference)
        => Result.Failure<IReadOnlyList<Finding>>(
            new Failure(code, message,
                        reference is null
                            ? null
                            : new Dictionary<string, object?> { ["reference"] = reference }));
}
