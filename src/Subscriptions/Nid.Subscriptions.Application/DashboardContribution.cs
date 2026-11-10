using Nid.Core.Contracts;
using Duration = Nid.Core.Contracts.Duration;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Subscriptions.Application;

/// <summary>
/// 2D1 spec §4: this calendar month's amortized and cash cost of the reader's visible active subscriptions, by the
/// household totals' rules (a purchase granting to several subscriptions is counted once in cash). Issue #12: with
/// <c>includeArchived</c>, archived ones too, each for its charges strictly before its archive day.
/// </summary>
public sealed class SubscriptionsCommitments(IVisibleItems visible, ISubscriptionStore store, IItemRegistry items, IHouseholdCalendar calendar)
    : ICommitmentsContribution
{
    public async Task<Result<CommitmentsPart>> CommitmentsAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct, bool includeArchived = false)
    {
        var window = CostWindow.Resolve(today, null, null);
        var horizon = Horizons.Resolve(today, null);
        var ids = (await visible.ForAsync(reader, scope, includeArchived, ct)).ToList();
        var inputs = await store.LoadManyAsync(ids, ct);
        var archivedOn = new Dictionary<ItemId, LocalDate>();
        if (includeArchived)
        {
            var zone = await calendar.ZoneAsync(ct);
            foreach (var (id, item) in await items.FindManyAsync(ids, ct))
                if (item.ArchivedAt is { } at)
                    archivedOn[id] = at.InZone(zone).Date;
        }

        var timelines = new List<Timeline>();
        foreach (var input in inputs)
        {
            if (!SubscriptionTimeline.Build(input, today, horizon).TryGetValue(out var timeline))
                continue; // as the list: a refused subscription is reported by its own detail
            if (archivedOn.TryGetValue(input.Subscription.Id, out var archived))
            {
                if (archived <= window.Start)
                    continue; // archived before this month: counts nowhere
                timeline = timeline! with { Charges = timeline.Charges.Where(c => c.Date < archived).ToList() };
            }
            timelines.Add(timeline!);
        }

        return Result.Success(new CommitmentsPart(
            CommitmentSources.Subscriptions, timelines.Count,
            Sum(HouseholdCost.AmortizedPieces(timelines, window)),
            Sum(HouseholdCost.CashPieces(timelines, window))));
    }

    private static IReadOnlyDictionary<Currency, decimal> Sum(IEnumerable<CostPiece> pieces)
        => pieces.GroupBy(p => p.Currency).ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));
}

/// <summary>
/// 2D2a spec §4: the first charge in the window (today included) of each phase whose cycle is longer than a month (monthly charges are already in « prélevés
/// ce mois-ci »), and the end of coverage of a subscription that ends within the window (an open-ended one expires at the
/// twelve-month horizon, never inside it).
/// </summary>
public sealed class SubscriptionsUpcoming(SubscriptionSet set) : IUpcomingEvents
{
    public async Task<Result<IReadOnlyList<UpcomingEvent>>> UpcomingAsync(PersonId reader, VisibilityScope scope, LocalDate today, LocalDate until, CancellationToken ct)
    {
        var horizon = Horizons.Resolve(today, null); // twelve months: past the 60-day window
        var events = new List<UpcomingEvent>();
        foreach (var (input, timeline, name, _, _) in await set.LoadAsync(reader, scope, today, horizon, ct))
        {
            var id = input.Subscription.Id;
            foreach (var first in ChargeCycles.FirstLongCycleCharges(input, timeline, today, until))
                events.Add(new UpcomingEvent(first.Date, UpcomingKinds.SubscriptionRenewal, id, "subscriptions", name, null, first.Price));
            if (ContractCancellation.Outlook(input, timeline, today) is { Status: CancellationStatus.Deadline, LastDayToCancel: { } last } && last >= today && last <= until)
                events.Add(new UpcomingEvent(last, UpcomingKinds.SubscriptionCancelDeadline, id, "subscriptions", name, null, null));
            if (timeline.ExpiresOn() is { } expires && expires >= today && expires <= until)
                events.Add(new UpcomingEvent(expires, UpcomingKinds.SubscriptionExpiry, id, "subscriptions", name, null, null));
        }
        return Result.Success<IReadOnlyList<UpcomingEvent>>(events);
    }
}
