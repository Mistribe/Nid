using System.Globalization;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using NodaTime;

namespace Nid.Subscriptions.Application;

/// <summary>
/// Issue #12: the yearly review's subscriptions over <c>(start, end]</c>, from the built set. An archived one's timeline already
/// ends at its archive day (<see cref="BuiltSubscription.ArchivedAt"/>): charges before it, coverage cut at it, so it expires on it
/// at the latest; its phase changes count before that day too.
/// </summary>
public static class ReviewMath
{
    private static readonly StringComparer Fr = StringComparer.Create(new CultureInfo("fr-FR"), true);

    /// <summary>The household charges dated in <c>(start, end]</c>, a purchase granting to several subscriptions once, as the list counts them.</summary>
    public static IReadOnlyList<CostPiece> Cost(IReadOnlyList<BuiltSubscription> set, LocalDate start, LocalDate end)
        => HouseholdCost.CashPieces(set.Select(b => b.Timeline).ToList(), CostWindow.Resolve(end, start.PlusDays(1), end.PlusDays(1)));

    /// <summary>The subscriptions whose first charge ever falls in the period.</summary>
    public static IReadOnlyList<SubscriptionReviewItem> Started(IReadOnlyList<BuiltSubscription> set, LocalDate start, LocalDate end)
        => Sorted(set.Where(b => b.Timeline.Charges.Count > 0)
            .Select(b => Item(b, b.Timeline.Charges.Min(c => c.Date)))
            .Where(i => i.On > start && i.On <= end));

    /// <summary>
    /// The subscriptions whose coverage ended in the period, by today, without successor (the savings' rule). An archived one ends
    /// on the earlier of its expiry and its archive day.
    /// </summary>
    public static IReadOnlyList<SubscriptionReviewItem> Ended(IReadOnlyList<BuiltSubscription> set, LocalDate start, LocalDate end, LocalDate today)
        => Sorted(set.Select(b => (Sub: b, On: b.Timeline.ExpiresOn()))
            .Where(x => x.On is { } on && on > start && on <= end && on <= today && !SpendingHistory.Replaced(x.Sub, set, on))
            .Select(x => Item(x.Sub, x.On!.Value)));

    /// <summary>Each phase following another at a different price or cycle, the later one starting in the period; a gap between the two does not matter, the change is dated on the restart.</summary>
    public static IReadOnlyList<SubscriptionPriceChange> PriceChanges(IReadOnlyList<BuiltSubscription> set, LocalDate start, LocalDate end)
    {
        var changes = new List<SubscriptionPriceChange>();
        foreach (var sub in set)
        {
            var phases = sub.Input.Phases.OrderBy(p => p.StartsOn).ToList();
            for (var i = 1; i < phases.Count; i++)
            {
                var (before, after) = (phases[i - 1], phases[i]);
                if (after.StartsOn <= start || after.StartsOn > end || !BeforeArchive(sub, after.StartsOn)
                    || (before.Price == after.Price && before.Every == after.Every))
                    continue;
                changes.Add(new SubscriptionPriceChange(sub.Input.Subscription.Id, sub.DisplayName, after.StartsOn,
                    before.Price.Amount, before.Price.Currency.Code, before.Every,
                    after.Price.Amount, after.Price.Currency.Code, after.Every, before.MaxCycles is not null));
            }
        }
        return changes.OrderBy(c => c.On).ThenBy(c => c.Name, Fr).ThenBy(c => c.Id.Value).ToList();
    }

    private static bool BeforeArchive(BuiltSubscription sub, LocalDate on) => sub.ArchivedOn is not { } archived || on < archived;

    private static SubscriptionReviewItem Item(BuiltSubscription sub, LocalDate on)
        => new(sub.Input.Subscription.Id, sub.DisplayName, sub.Provider?.Name, on);

    private static IReadOnlyList<SubscriptionReviewItem> Sorted(IEnumerable<SubscriptionReviewItem> items)
        => items.OrderBy(i => i.On).ThenBy(i => i.Name, Fr).ThenBy(i => i.Id.Value).ToList();
}
