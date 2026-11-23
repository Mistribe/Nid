using Nid.Core.Contracts;

namespace Nid.Subscriptions.Application;

/// <summary>Issue #514: the event kinds of the subscriptions (<see cref="SubscriptionsUpcoming"/>), in the dashboard's words (web/src/locales/fr.json, dashboard.upcoming).</summary>
public sealed class SubscriptionsUpcomingKinds : IUpcomingKinds
{
    public IReadOnlyList<UpcomingKind> Kinds { get; } =
    [
        new(UpcomingKinds.SubscriptionRenewal, UpcomingCadence.Recurring, _ => "Renouvellement"),
        new(UpcomingKinds.SubscriptionExpiry, UpcomingCadence.Milestone, _ => "Expire"),
        new(UpcomingKinds.SubscriptionCancelDeadline, UpcomingCadence.Milestone, _ => "Dernier jour pour résilier"),
        // Issue #244: the first paid charge after a free period, and the first charge of a dearer phase.
        new(UpcomingKinds.SubscriptionTrialEnd, UpcomingCadence.Milestone, _ => "Fin de l'essai gratuit"),
        new(UpcomingKinds.SubscriptionPriceIncrease, UpcomingCadence.Milestone, _ => "Hausse de prix"),
    ];
}
