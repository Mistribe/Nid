using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Subscriptions.Application;

/// <summary>
/// Notifications design §2.3: the dashboard's renewals and expiries of the reader's own subscriptions, and the overview's
/// « ends without renewal » watch, as signals. No amount travels: a notification says what and when, the screen has the figures.
/// </summary>
public sealed class SubscriptionsNotificationSource(SubscriptionsUpcoming upcoming, SubscriptionsOverviewReader overview) : INotificationSource
{
    private static readonly IReadOnlyDictionary<string, string> NoParams = new Dictionary<string, string>();

    public string Name => "subscriptions";

    public IReadOnlyList<NotificationKind> Kinds { get; } =
    [
        Due(UpcomingKinds.SubscriptionRenewal, 7, "Un abonnement se renouvelle"),
        Due(UpcomingKinds.SubscriptionExpiry, 14, "Un abonnement prend fin"),
        Due(UpcomingKinds.SubscriptionCancelDeadline, 30, "Dernier jour pour résilier un contrat"),
        Due(UpcomingKinds.SubscriptionTrialEnd, 7, "Une période gratuite se termine"),
        Due(UpcomingKinds.SubscriptionPriceIncrease, 7, "Le prix d'un abonnement augmente"),
        new(OverviewCodes.EndsWithoutRenewal, "finding", NotificationLevel.Info, "subscriptions", null)
        {
            Template = new("Un abonnement s'arrête sans renouvellement")
            {
                // The watch's date is in its key.
                Suffix = f => (f.DueOn ?? f.KeyDate("yyyy-MM-dd")) is { } on ? $" — le {NotificationFacts.Day(on)}" : "",
                Link = Page,
            },
        },
    ];

    private static NotificationKind Due(string kind, int leadDays, string title)
        => new(kind, "due_date", NotificationLevel.Action, "subscriptions", leadDays) { Template = new(title) { Link = Page } };

    private static string Page(NotificationFacts f) => f.ItemPage("subscriptions");

    public async Task<Result<IReadOnlyList<Signal>>> SignalsAsync(PersonId reader, LocalDate today, LocalDate horizon, CancellationToken ct)
    {
        var signals = new List<Signal>();

        var events = await upcoming.UpcomingAsync(reader, VisibilityScope.Me, today, horizon, ct);
        if (!events.TryGetValue(out var list))
            return Result.Failure<IReadOnlyList<Signal>>(events.TryGetFailure(out var failure) ? failure! : new Failure("subscriptions.unknown", "The upcoming events could not be read.", null));
        foreach (var e in list!.Where(e => e.Kind is UpcomingKinds.SubscriptionRenewal or UpcomingKinds.SubscriptionExpiry or UpcomingKinds.SubscriptionCancelDeadline
                     or UpcomingKinds.SubscriptionTrialEnd or UpcomingKinds.SubscriptionPriceIncrease))
            signals.Add(new Signal(SignalKeys.Dated(e.Kind, e.Item, e.On), e.Kind, [e.Item], e.On, NoParams));

        foreach (var watch in (await overview.ReadAsync(reader, VisibilityScope.Me, today, ct)).Watch
                     .Where(w => w.Code == OverviewCodes.EndsWithoutRenewal))
            foreach (var id in watch.ItemIds.Select(i => new ItemId(i)))
                signals.Add(new Signal(
                    watch.Date is { } date ? SignalKeys.Dated(watch.Code, id, date) : SignalKeys.Undated(watch.Code, id),
                    watch.Code, [id], null, NoParams));

        return Result.Success<IReadOnlyList<Signal>>(signals);
    }
}
