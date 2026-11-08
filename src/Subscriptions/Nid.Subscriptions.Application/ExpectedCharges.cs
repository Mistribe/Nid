using System.Globalization;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Subscriptions.Application;

/// <summary>5C spec §6.1: the phase charges of the reader's visible subscriptions; a purchase is a one-off, not a debit to expect.</summary>
public sealed class SubscriptionExpectedCharges(SubscriptionSet set, IClock clock) : IExpectedCharges
{
    public async Task<Result<IReadOnlyList<ExpectedCharge>>> ChargesAsync(PersonId reader, LocalDate from, LocalDate to, CancellationToken ct)
    {
        var today = clock.GetCurrentInstant().InUtc().Date;
        var charges = new List<ExpectedCharge>();
        foreach (var (input, timeline, name, _, provider) in await set.LoadAsync(reader, VisibilityScope.Me, today, to > today ? to : today, ct))
        foreach (var charge in timeline.Charges)
            if (charge.Origin is ChargeOrigin.FromPhase && charge.Date >= from && charge.Date <= to && charge.Price.Amount != 0m)
                charges.Add(new ExpectedCharge("subscriptions", input.Subscription.Id, name, ChargeKind.Subscription,
                    charge.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), charge.Date, charge.Price, false, 5,
                    provider is null ? [name] : [name, provider.Name]));
        return Result.Success<IReadOnlyList<ExpectedCharge>>(charges);
    }
}
