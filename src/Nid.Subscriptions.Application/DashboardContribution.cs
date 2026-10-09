using Nid.Core.Contracts;
using Duration = Nid.Core.Contracts.Duration;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Subscriptions.Application;

/// <summary>
/// 2D1 spec §4: this calendar month's amortized and cash cost of the reader's visible active subscriptions, by the
/// household totals' rules (a purchase granting to several subscriptions is counted once in cash).
/// </summary>
public sealed class SubscriptionsCommitments(IVisibleItems visible, ISubscriptionStore store) : ICommitmentsContribution
{
    public async Task<Result<CommitmentsPart>> CommitmentsAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct)
    {
        var window = CostWindow.Resolve(today, null, null);
        var horizon = Horizons.Resolve(today, null);
        var inputs = await store.LoadManyAsync((await visible.ForAsync(reader, scope, ct)).ToList(), ct);

        var timelines = new List<Timeline>();
        foreach (var input in inputs)
            if (SubscriptionTimeline.Build(input, today, horizon).TryGetValue(out var timeline))
                timelines.Add(timeline!); // as the list: a refused subscription is reported by its own detail

        return Result.Success(new CommitmentsPart(
            timelines.Count,
            Sum(HouseholdCost.AmortizedPieces(timelines, window)),
            Sum(HouseholdCost.CashPieces(timelines, window))));
    }

    private static IReadOnlyDictionary<Currency, decimal> Sum(IEnumerable<CostPiece> pieces)
        => pieces.GroupBy(p => p.Currency).ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));
}

/// <summary>
/// 2D2a spec §4: the next charge of a phase whose cycle is longer than a month (monthly charges are already in « prélevés
/// ce mois-ci »), and the end of coverage of a subscription that ends within the window (an open-ended one expires at the
/// twelve-month horizon, never inside it).
/// </summary>
public sealed class SubscriptionsUpcoming(IVisibleItems visible, ISubscriptionStore store, IItemRegistry items) : IUpcomingEvents
{
    public async Task<Result<IReadOnlyList<UpcomingEvent>>> UpcomingAsync(PersonId reader, VisibilityScope scope, LocalDate today, LocalDate until, CancellationToken ct)
    {
        var horizon = Horizons.Resolve(today, null); // twelve months: past the 60-day window
        var events = new List<UpcomingEvent>();
        foreach (var input in await store.LoadManyAsync((await visible.ForAsync(reader, scope, ct)).ToList(), ct))
        {
            if (!SubscriptionTimeline.Build(input, today, horizon).TryGetValue(out var timeline))
                continue; // as the list: a refused subscription is reported by its own detail
            var id = input.Subscription.Id;
            var name = (await items.FindAsync(id, ct))?.DisplayName ?? string.Empty;
            if (timeline!.NextCharge() is { } next)
            {
                var phase = next.Origin is ChargeOrigin.FromPhase from ? input.Phases.FirstOrDefault(p => p.Id == from.PhaseId) : null;
                if (phase is not null && LongerThanAMonth(phase.Every) && next.Date >= today && next.Date <= until)
                    events.Add(new UpcomingEvent(next.Date, UpcomingKinds.SubscriptionRenewal, id, "subscriptions", name, null, next.Price));
            }
            if (timeline.ExpiresOn() is { } expires && expires >= today && expires <= until)
                events.Add(new UpcomingEvent(expires, UpcomingKinds.SubscriptionExpiry, id, "subscriptions", name, null, null));
        }
        return Result.Success<IReadOnlyList<UpcomingEvent>>(events);
    }

    /// <summary>At least two months, any number of years, more than 31 days or more than 4 weeks.</summary>
    private static bool LongerThanAMonth(Duration every) => every.Unit switch
    {
        DurationUnit.Year => true,
        DurationUnit.Month => every.Count >= 2,
        DurationUnit.Week => every.Count > 4,
        _ => every.Count > 31,
    };
}
