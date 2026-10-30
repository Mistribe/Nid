using NodaTime;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using UnambitiousFx.Functional.Failures;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Subscriptions.Domain;

/// <summary>
/// The last day to cancel a contract, from the terms the household typed (design 2026-10-30-contract-terms-insurance §3).
/// Pure: nothing it returns is stored.
/// </summary>
public static class ContractCancellation
{
    private static readonly Duration OneYear = Duration.Create(1, DurationUnit.Year).TryGetValue(out var year)
        ? year
        : throw new InvalidOperationException("One year is a valid duration.");

    public static CancellationOutlook Outlook(SubscriptionInput input, Timeline timeline, LocalDate today)
    {
        if (input.Subscription.Terms is not { IsEmpty: false } terms || input.Subscription.CancelledAt is not null
            || !input.Subscription.AutoRenew)
            return CancellationOutlook.None;

        if (timeline.NextCharge() is null && timeline.ExpiresOn() is { } end && end < today)
            return CancellationOutlook.None;

        var commitment = terms.CommitmentEndsOn;
        var freedom = terms.CancellableFrom ?? (terms.AnniversaryOn is null ? commitment : null);
        if (freedom is { } free && free <= today && (commitment is not { } c || c <= today))
            return new CancellationOutlook(CancellationStatus.FreeNow, null, null, null);

        if (terms.AnniversaryOn is { } anchor)
        {
            // Stops before the walk could pass LocalDate.Max: the free-from step below answers instead.
            for (var k = 0; anchor.Year + k < 9000; k++)
            {
                var anniversary = OneYear.AddTo(anchor, k);
                if (terms.CancellableFrom is { } limit && anniversary > limit)
                    break;

                if (commitment is { } floor && anniversary < floor)
                    continue;

                var last = terms.Notice is { } notice ? notice.AddTo(anniversary, -1) : anniversary;
                if (last >= today)
                    return new CancellationOutlook(CancellationStatus.Deadline, last, anniversary, null);
            }
        }

        if (freedom is { } from)
        {
            var on = commitment is { } floor && floor > from ? floor : from;
            return new CancellationOutlook(CancellationStatus.FreeFrom, null, null, on);
        }

        return CancellationOutlook.None;
    }
}
