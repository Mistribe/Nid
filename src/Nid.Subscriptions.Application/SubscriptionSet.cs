using Nid.Core.Contracts;
using Duration = Nid.Core.Contracts.Duration;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using NodaTime;

namespace Nid.Subscriptions.Application;

public sealed record BuiltSubscription(
    SubscriptionInput Input, Timeline Timeline, string DisplayName, IReadOnlyList<ItemOwnerView> Owners, ProviderSummaryView? Provider);

/// <summary>The reader's visible subscriptions with their timeline, name, owners and provider, loaded in a fixed number of queries.</summary>
public sealed class SubscriptionSet(IVisibleItems visible, ISubscriptionStore store, IItemRegistry items, IProviderStore providers)
{
    public async Task<IReadOnlyList<BuiltSubscription>> LoadAsync(
        PersonId reader, VisibilityScope scope, LocalDate today, LocalDate horizon, CancellationToken ct)
    {
        var inputs = await store.LoadManyAsync((await visible.ForAsync(reader, scope, ct)).ToList(), ct);
        var names = await items.FindManyAsync(inputs.Select(i => i.Subscription.Id).ToList(), ct); // batched reads spec §3

        var built = new List<(SubscriptionInput Input, Timeline Timeline)>(inputs.Count);
        foreach (var input in inputs)
            if (SubscriptionTimeline.Build(input, today, horizon).TryGetValue(out var timeline))
                built.Add((input, timeline!)); // a refused subscription is reported by its own detail endpoint, not by hiding the list

        // One provider query for the whole set; every subscription in it is visible, so is its provider (design §5).
        var providerViews = await ProviderLookup.ForAsync(store, providers, built.Select(b => b.Input.Subscription.Id).ToList(), ct);

        return built.Select(b =>
        {
            var id = b.Input.Subscription.Id;
            var item = names.GetValueOrDefault(id);
            return new BuiltSubscription(b.Input, b.Timeline, item?.DisplayName ?? "", item?.OwnerViews() ?? [], providerViews.GetValueOrDefault(id));
        }).ToList();
    }
}

public static class ChargeCycles
{
    /// <summary>At least two months, any number of years, more than 31 days or more than 4 weeks.</summary>
    public static bool LongerThanAMonth(Duration every) => every.Unit switch
    {
        DurationUnit.Year => true,
        DurationUnit.Month => every.Count >= 2,
        DurationUnit.Week => every.Count > 4,
        _ => every.Count > 31,
    };

    /// <summary>The phase a charge comes from; null for a purchase.</summary>
    public static BillingPhase? PhaseOf(SubscriptionInput input, Charge charge)
        => charge.Origin is ChargeOrigin.FromPhase from ? input.Phases.FirstOrDefault(p => p.Id == from.PhaseId) : null;

    /// <summary>
    /// The first charge in <c>[from, until]</c> of each phase longer than a month: a monthly phase charging first must not hide a
    /// yearly one after it.
    /// </summary>
    public static IEnumerable<Charge> FirstLongCycleCharges(SubscriptionInput input, Timeline timeline, LocalDate from, LocalDate until)
        => timeline.Charges
            .Where(c => c.Date >= from && c.Date <= until)
            .Select(c => (Charge: c, Phase: PhaseOf(input, c)))
            .Where(x => x.Phase is not null && LongerThanAMonth(x.Phase.Every))
            .GroupBy(x => x.Phase!.Id)
            .Select(g => g.MinBy(x => x.Charge.Date).Charge);
}
