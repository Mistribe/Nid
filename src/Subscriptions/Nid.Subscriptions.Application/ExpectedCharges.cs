using System.Globalization;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Subscriptions.Application;

/// <summary>5C spec §6.1: the phase charges of the subscriptions the reader sees in the scope; a purchase is a one-off, not a debit to expect.</summary>
public sealed class SubscriptionExpectedCharges(SubscriptionSet set, IHouseholdCalendar calendar) : IExpectedCharges
{
    /// <summary>Issue #514: a subscription's charge, on the subscriptions' page.</summary>
    public static readonly ChargeKind Subscription = new(ChargeKinds.Subscription, CalendarModules.Subscriptions);

    public async Task<Result<IReadOnlyList<ExpectedCharge>>> ChargesAsync(PersonId reader, VisibilityScope scope, LocalDate from, LocalDate to, CancellationToken ct)
    {
        var today = await calendar.TodayAsync(ct);
        var charges = new List<ExpectedCharge>();
        foreach (var (input, timeline, name, _, provider) in await set.LoadAsync(reader, scope, today, to > today ? to : today, ct))
        foreach (var charge in timeline.Charges)
            if (charge.Origin is ChargeOrigin.FromPhase && charge.Date >= from && charge.Date <= to && charge.Price.Amount != 0m)
                charges.Add(new ExpectedCharge("subscriptions", input.Subscription.Id, name, Subscription,
                    charge.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), charge.Date, charge.Price, false, 5,
                    provider is null ? [name] : [name, provider.Name]));
        return Result.Success<IReadOnlyList<ExpectedCharge>>(charges);
    }
}

/// <summary>
/// A subscription's purchases (a prepaid pack) in the window: already recorded, so a bank line may be linked to one, though no debit is
/// expected (<see cref="IRecordedCharges"/>). A refund or a free grant has nothing to link.
/// </summary>
public sealed class SubscriptionPurchaseCharges(SubscriptionSet set, IHouseholdCalendar calendar) : IRecordedCharges
{
    public static readonly ChargeKind Purchase = new(ChargeKinds.Purchase, CalendarModules.Subscriptions);

    public async Task<Result<IReadOnlyList<ExpectedCharge>>> ChargesAsync(PersonId reader, VisibilityScope scope, LocalDate from, LocalDate to, CancellationToken ct)
    {
        var today = await calendar.TodayAsync(ct);
        var charges = new List<ExpectedCharge>();
        foreach (var (input, timeline, name, _, provider) in await set.LoadAsync(reader, scope, today, to > today ? to : today, ct))
        {
            var seen = new HashSet<PurchaseId>();
            foreach (var charge in timeline.Charges)
                // A purchase granting several periods charges once per grant: one link target.
                if (charge.Origin is ChargeOrigin.FromPurchase p && charge.Date >= from && charge.Date <= to && charge.Price.Amount > 0m && seen.Add(p.PurchaseId))
                    charges.Add(new ExpectedCharge("subscriptions", input.Subscription.Id, name, Purchase, $"purchase:{p.PurchaseId.Value:D}", charge.Date,
                        charge.Price, false, 5, provider is null ? [name] : [name, provider.Name]));
        }
        return Result.Success<IReadOnlyList<ExpectedCharge>>(charges);
    }
}
