using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using NodaTime;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Subscriptions.Domain;

/// <summary>
/// A visible subscription with its timeline, as the reads build it: what the calculations over a set of subscriptions read of it.
/// An archived one's timeline already ends at its archive day (<see cref="ArchivedOn"/>).
/// </summary>
public interface IBuiltSubscription
{
    SubscriptionInput Input { get; }
    Timeline Timeline { get; }
    string DisplayName { get; }
    string? ProviderKey { get; }
    string? ProviderName { get; }
    LocalDate? ArchivedOn { get; }
}

/// <summary>The charges of a set of subscriptions, and the cycles they come from.</summary>
public static class ChargeRules
{
    /// <summary>At least two months, any number of years, more than 31 days or more than 4 weeks.</summary>
    public static bool LongerThanAMonth(Duration every) => every.Unit switch
    {
        DurationUnit.Year => true,
        DurationUnit.Month => every.Count >= 2,
        DurationUnit.Week => every.Count > 4,
        _ => every.Count > 31,
    };

    /// <summary>Each charge of the set once: a purchase granting to several subscriptions left the account once (D5).</summary>
    public static IEnumerable<(T Sub, Charge Charge)> HouseholdCharges<T>(IEnumerable<T> set) where T : IBuiltSubscription
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
}

/// <summary>
/// The past charges of the timelines (#395 to #398): each turned into a piece converted at its own date. The savings read the
/// charges an ended subscription no longer makes, the price rises the dearer phases.
/// </summary>
public static class SpendingHistory
{
    public static CostPiece Piece(Charge charge) => new(charge.Date, charge.Price.Currency, charge.Price.Amount, charge.IsProjected);

    /// <summary>The charges made by today, the first one first.</summary>
    public static IEnumerable<Charge> Past(Timeline timeline) => timeline.Charges.Where(c => !c.IsProjected);

    /// <summary>
    /// The charges an ended subscription would have made since its end (the first day it was no longer covered), at the
    /// price and cycle of its last phase charge, up to today. None for one still running, one replaced by another subscription
    /// of the same provider (covered after its end), one whose last charge was free, or one paid by purchases only.
    /// </summary>
    public static (LocalDate End, Charge Last, BillingPhase Phase, IReadOnlyList<CostPiece> Pieces)? Forgone<T>(
        T sub, IEnumerable<T> others, LocalDate today) where T : IBuiltSubscription
    {
        if (sub.Timeline.ExpiresOn() is not { } end || end >= today)
            return null;
        if (sub.Timeline.Charges.LastOrDefault(c => c.Origin is ChargeOrigin.FromPhase && !c.IsProjected) is not { } last
            || last.Price.Amount <= 0m || ChargeRules.PhaseOf(sub.Input, last) is not { } phase)
            return null;
        if (Replaced(sub, others, end))
            return null;

        var pieces = new List<CostPiece>();
        for (var on = end; on <= today; on = phase.Every.AddTo(end, pieces.Count))
            pieces.Add(new CostPiece(on, last.Price.Currency, last.Price.Amount, false));
        return (end, last, phase, pieces);
    }

    /// <summary>Another subscription of the same provider still covered after <paramref name="end"/>: the ended one has a successor.</summary>
    public static bool Replaced<T>(T sub, IEnumerable<T> others, LocalDate end) where T : IBuiltSubscription
        => sub.ProviderKey is { } provider && others.Any(o => o.Input.Subscription.Id != sub.Input.Subscription.Id
            && o.ProviderKey == provider && o.Timeline.ExpiresOn() is { } until && until > end);

    /// <summary>Every phase change of the set in <c>(today − 12 months, today]</c> to a phase that costs more a year, in one currency.</summary>
    public static IEnumerable<(T Sub, BillingPhase From, BillingPhase To, CostPiece Difference)> Rises<T>(
        IEnumerable<T> set, LocalDate today) where T : IBuiltSubscription
    {
        foreach (var sub in set)
        {
            var phases = sub.Input.Phases.OrderBy(p => p.StartsOn).ToList();
            for (var i = 1; i < phases.Count; i++)
            {
                var (from, to) = (phases[i - 1], phases[i]);
                if (to.StartsOn <= today.PlusMonths(-12) || to.StartsOn > today || to.Price.Currency != from.Price.Currency)
                    continue;
                var difference = Yearly(to, to.StartsOn) - Yearly(from, to.StartsOn);
                if (difference > 0m)
                    yield return (sub, from, to, new CostPiece(to.StartsOn, to.Price.Currency, difference, false));
            }
        }
    }

    /// <summary>A phase's price times the charges it makes in the year from <paramref name="on"/>: 12 monthly, 52 or 53 weekly.</summary>
    public static decimal Yearly(BillingPhase phase, LocalDate on)
    {
        var cycles = 0;
        while (phase.Every.AddTo(on, cycles) < on.PlusYears(1))
            cycles++;
        return phase.Price.Amount * cycles;
    }
}
