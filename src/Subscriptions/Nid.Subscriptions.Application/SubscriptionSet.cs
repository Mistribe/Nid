using Nid.Core.Contracts;
using Duration = Nid.Core.Contracts.Duration;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using NodaTime;

namespace Nid.Subscriptions.Application;

public sealed record BuiltSubscription(
    SubscriptionInput Input, Timeline Timeline, string DisplayName, IReadOnlyList<ItemOwnerView> Owners, ProviderSummaryView? Provider)
{
    /// <summary>Issue #12: the household day it was archived on; its timeline then holds only the charges before that day.</summary>
    public LocalDate? ArchivedOn { get; init; }
}

/// <summary>The reader's visible subscriptions with their timeline, name, owners and provider, loaded in a fixed number of queries.</summary>
public sealed class SubscriptionSet(IVisibleItems visible, ISubscriptionStore store, IItemRegistry items, IProviderStore providers, IHouseholdCalendar calendar)
{
    /// <param name="includeArchived">Issue #12: archived ones too, each for its charges strictly before its archive day.</param>
    public async Task<IReadOnlyList<BuiltSubscription>> LoadAsync(
        PersonId reader, VisibilityScope scope, LocalDate today, LocalDate horizon, CancellationToken ct, bool includeArchived = false)
    {
        var inputs = await store.LoadManyAsync((await visible.ForAsync(reader, scope, includeArchived, ct)).ToList(), ct);
        var names = await items.FindManyAsync(inputs.Select(i => i.Subscription.Id).ToList(), ct); // batched reads spec §3
        var zone = includeArchived ? await calendar.ZoneAsync(ct) : null;

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
            LocalDate? archivedOn = item?.ArchivedAt is { } at && zone is not null ? at.InZone(zone).Date : null;
            var timeline = archivedOn is { } day ? b.Timeline with { Charges = b.Timeline.Charges.Where(c => c.Date < day).ToList() } : b.Timeline;
            return new BuiltSubscription(b.Input, timeline, item?.DisplayName ?? "", item?.OwnerViews() ?? [], providerViews.GetValueOrDefault(id))
                { ArchivedOn = archivedOn };
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

    // Each charge of the set once: a purchase granting to several subscriptions left the account once (D5).
    internal static IEnumerable<(BuiltSubscription Sub, Charge Charge)> HouseholdCharges(IEnumerable<BuiltSubscription> set)
    {
        var seen = new HashSet<PurchaseId>();
        foreach (var b in set)
        foreach (var charge in b.Timeline.Charges)
            if (charge.Origin is not ChargeOrigin.FromPurchase p || seen.Add(p.PurchaseId))
                yield return (b, charge);
    }

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
